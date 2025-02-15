import ARKit
import RealityKit
import MetalKit
import Accelerate
import Foundation
import Network
import CoreGraphics
import UIKit // For UIImage, cgImage, etc.

// MARK: - Global Variables

var arKitSession = ARKitSession()
var isRunning = false

let mtlDevice: MTLDevice = MTLCreateSystemDefaultDevice()!
var commandQueue: MTLCommandQueue!
var textureCache: CVMetalTextureCache!

var currentTexture: MTLTexture? = nil
var pointer: UnsafeMutableRawPointer? = nil

var chosenCameraWidth: Int32 = 1920
var chosenCameraHeight: Int32 = 1080

var currentIntrinsics: simd_float3x3 = .init()
var currentExtrinsics: simd_float4x4 = .init()

// NEW: TCP Connection
var tcpConnection: NWConnection?
var latestMaskData: Data? = nil
let maskDataQueue = DispatchQueue(label: "maskData.queue")

// MARK: - C-Style Exported Functions

@_cdecl("startCapture")
public func startCapture() {
    print("startCapture() called.")
    isRunning = true
    
    connectToPythonServer(host: "192.168.1.84", port: 12345)  // Example IP/port
    
    Task {
        let formats = CameraVideoFormat.supportedVideoFormats(for: .main, cameraPositions: [.left])
        formats.forEach { fmt in
            let size = fmt.frameSize
            print("Supported format: \(size.width)x\(size.height)")
        }
        guard !formats.isEmpty else {
            print("No camera formats found for main left camera.")
            return
        }
        
        // pick the highest
        let firstFormat = formats.max { $0.frameSize.height < $1.frameSize.height }!
        
        chosenCameraWidth = Int32(firstFormat.frameSize.width)
        chosenCameraHeight = Int32(firstFormat.frameSize.height)
        print("Chosen camera format: \(chosenCameraWidth)x\(chosenCameraHeight)")

        let statuses = await arKitSession.queryAuthorization(for: [.cameraAccess])
        // if statuses[.cameraAccess] != .authorized { ... }

        let cameraProvider = CameraFrameProvider()
        do {
            try await arKitSession.run([cameraProvider])
        } catch {
            print("Failed to run ARKitSession:", error)
            return
        }
        
        print("ARKit session running. Beginning camera capture loop...")
        
        guard let updates = cameraProvider.cameraFrameUpdates(for: firstFormat) else {
            print("No cameraFrameUpdates available.")
            return
        }
        
        for await frame in updates {
            if !isRunning { break }
            
            let pixelBuffer = frame.primarySample.pixelBuffer
            let parameters = frame.primarySample.parameters
            
            currentIntrinsics = parameters.intrinsics
            currentExtrinsics = parameters.extrinsics
            
            // (A) Send to server at half resolution
            sendFrameToServer(pixelBuffer: pixelBuffer)
            
            // (B) Convert & create MTLTexture (full resolution for Unity)
            createTexture(from: pixelBuffer)
        }
        
        print("Camera capture loop finished.")
    }
}

@_cdecl("stopCapture")
public func stopCapture() {
    print("stopCapture() called.")
    isRunning = false
    arKitSession.stop()
    print("ARKit session stopped.")
    
    // close TCP
    tcpConnection?.cancel()
    tcpConnection = nil
}

@_cdecl("getTexturePointer")
public func getTexturePointer() -> UnsafeMutableRawPointer? {
    return pointer
}

@_cdecl("getCameraChosenWidth")
public func getCameraChosenWidth() -> Int32 {
    return chosenCameraWidth
}

@_cdecl("getCameraChosenHeight")
public func getCameraChosenHeight() -> Int32 {
    return chosenCameraHeight
}

@_cdecl("getIntrinsicsMatrix")
public func getIntrinsicsMatrix() -> simd_float3x3 {
    return currentIntrinsics
}

@_cdecl("getExtrinsicsMatrix")
public func getExtrinsicsMatrix() -> simd_float4x4 {
    return currentExtrinsics
}

// MARK: - Networking

func connectToPythonServer(host: String, port: Int) {
    guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else {
        print("Invalid port number.")
        return
    }
    let tcpParams = NWParameters.tcp
    tcpConnection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: tcpParams)
    
    tcpConnection?.stateUpdateHandler = { state in
        switch state {
        case .ready:
            print("TCP connected to server.")
            startReadingFromServer()
        case .failed(let err):
            print("TCP connect failed: \(err)")
        default:
            break
        }
    }
    
    tcpConnection?.start(queue: .global(qos: .background))
}

func startReadingFromServer() {
    tcpConnection?.receive(minimumIncompleteLength: 4, maximumLength: 4) { data, _, isComplete, error in
        if let e = error {
            print("Receive length error: \(e)")
            return
        }
        guard let lengthData = data, lengthData.count == 4 else {
            print("No length data.")
            return
        }
        let length = lengthData.withUnsafeBytes { $0.load(as: Int32.self).littleEndian }
        if length <= 0 {
            print("Invalid length \(length).")
            return
        }
        
        tcpConnection?.receive(minimumIncompleteLength: Int(length), maximumLength: Int(length)) { content, _, isComplete2, err2 in
            if let e2 = err2 {
                print("Receive content error: \(e2)")
                return
            }
            guard let maskBytes = content, maskBytes.count == Int(length) else {
                print("Mask data size mismatch.")
                return
            }
            maskDataQueue.async {
                latestMaskData = maskBytes
            }
            // continue reading next
            startReadingFromServer()
        }
    }
}

func sendFrameToServer(pixelBuffer: CVPixelBuffer) {
    // encode to JPEG at half resolution
    guard let jpegData = encodePixelBufferToHalfResJPEG(pixelBuffer: pixelBuffer, quality: 0.5) else {
        return
    }
    let length = Int32(jpegData.count).littleEndian
    var lengthData = withUnsafeBytes(of: length) { Data($0) }
    lengthData.append(jpegData)
    
    tcpConnection?.send(content: lengthData, completion: .contentProcessed({ error in
        if let e = error {
            print("Send error:", e)
        }
    }))
}

// MARK: - half resolution method
func encodePixelBufferToHalfResJPEG(pixelBuffer: CVPixelBuffer, quality: CGFloat) -> Data? {
    // 1) Convert pixelBuffer -> CGImage
    guard let cgImage = pixelBufferToCGImage(pixelBuffer) else { return nil }
    // 2) Make UIImage
    let uiImage = UIImage(cgImage: cgImage)
    // 3) Scale to half
    let halfUIImage = scaleImage(uiImage, scale: 0.5)
    // 4) JPEG encode
    return halfUIImage.jpegData(compressionQuality: quality)
}

func scaleImage(_ image: UIImage, scale: CGFloat) -> UIImage {
    let newWidth = image.size.width * scale
    let newHeight = image.size.height * scale
    let newSize = CGSize(width: newWidth, height: newHeight)

    UIGraphicsBeginImageContextWithOptions(newSize, false, 1.0)
    image.draw(in: CGRect(origin: .zero, size: newSize))
    let scaled = UIGraphicsGetImageFromCurrentImageContext()
    UIGraphicsEndImageContext()

    return scaled ?? image
}

func pixelBufferToCGImage(_ pixelBuffer: CVPixelBuffer) -> CGImage? {
    let ciImage = CIImage(cvPixelBuffer: pixelBuffer)
    let context = CIContext(options: nil)
    return context.createCGImage(ciImage, from: ciImage.extent)
}

// MARK: - Creating/Overriding the MTLTexture

func createTexture(from pixelBuffer: CVPixelBuffer) {
    // 1) Attempt YUV->BGRA if extension is available
    guard let bgraBuffer = try? pixelBuffer.toBGRA() else {
        return
    }
    
    let width = CVPixelBufferGetWidth(bgraBuffer)
    let height = CVPixelBufferGetHeight(bgraBuffer)
    
    if textureCache == nil {
        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, mtlDevice, nil, &textureCache)
    }
    if commandQueue == nil {
        commandQueue = mtlDevice.makeCommandQueue()
    }
    
    var cvMetalTex: CVMetalTexture?
    let creationResult = CVMetalTextureCacheCreateTextureFromImage(
        kCFAllocatorDefault,
        textureCache,
        bgraBuffer,
        nil,
        .bgra8Unorm_srgb,
        width,
        height,
        0,
        &cvMetalTex
    )
    guard
        creationResult == kCVReturnSuccess,
        let cvMTLTex = cvMetalTex,
        let sourceTexture = CVMetalTextureGetTexture(cvMTLTex)
    else {
        print("CVMetalTextureCacheCreateTextureFromImage failed.")
        return
    }
    
    // If we have a mask from server, do minimal overlay
    if let maskData = maskDataQueue.sync(execute: { latestMaskData }) {
        // decode mask as PNG
        if let maskCG = decodePNGToCGImage(maskData) {
            if let finalCG = overlayMaskCPU(bgTexture: sourceTexture, maskCG: maskCG) {
                if let finalMTL = cgImageToMTLTexture(finalCG) {
                    finalizeTextureCopy(inTex: finalMTL)
                    return
                }
            }
        }
    }

    // fallback: no mask or fail -> just copy
    finalizeTextureCopy(inTex: sourceTexture)
}

func finalizeTextureCopy(inTex: MTLTexture) {
    if currentTexture == nil {
        let desc = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: inTex.pixelFormat,
            width: inTex.width,
            height: inTex.height,
            mipmapped: false
        )
        desc.usage = [.shaderRead]
        currentTexture = mtlDevice.makeTexture(descriptor: desc)
    }
    
    guard let finalTex = currentTexture,
          let cmdBuf = commandQueue?.makeCommandBuffer(),
          let blitEnc = cmdBuf.makeBlitCommandEncoder()
    else {
        return
    }
    
    let region = MTLRegionMake2D(0, 0, inTex.width, inTex.height)
    blitEnc.copy(from: inTex,
                 sourceSlice: 0,
                 sourceLevel: 0,
                 sourceOrigin: region.origin,
                 sourceSize: region.size,
                 to: finalTex,
                 destinationSlice: 0,
                 destinationLevel: 0,
                 destinationOrigin: region.origin)
    blitEnc.endEncoding()
    cmdBuf.commit()
    cmdBuf.waitUntilCompleted()
    
    if pointer == nil {
        pointer = Unmanaged.passUnretained(finalTex).toOpaque()
    }
}

// decode PNG -> CGImage
func decodePNGToCGImage(_ pngData: Data) -> CGImage? {
    guard let uiImg = UIImage(data: pngData) else { return nil }
    return uiImg.cgImage
}

// CPU overlay
func overlayMaskCPU(bgTexture: MTLTexture, maskCG: CGImage) -> CGImage? {
    guard let bgCG = textureToCGImage(bgTexture) else { return nil }
    
    let width = bgCG.width
    let height = bgCG.height
    
    guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else { return nil }
    guard let context = CGContext(
        data: nil,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: width * 4,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue
    ) else { return nil }
    
    // draw original
    context.draw(bgCG, in: CGRect(x: 0, y: 0, width: width, height: height))
    
    // We'll scale mask to fit if needed
    let destRect = CGRect(x: 0, y: 0, width: width, height: height)
    context.saveGState()
    context.setFillColor(UIColor(red: 0, green: 1, blue: 0, alpha: 0.4).cgColor)
    context.clip(to: destRect, mask: maskCG)
    context.fill(destRect)
    context.restoreGState()
    
    return context.makeImage()
}

// Convert MTLTexture -> CGImage
func textureToCGImage(_ texture: MTLTexture) -> CGImage? {
    let width = texture.width
    let height = texture.height
    let rowBytes = width * 4
    
    var bgraBytes = [UInt8](repeating: 0, count: rowBytes * height)
    
    let region = MTLRegionMake2D(0, 0, width, height)
    texture.getBytes(
        &bgraBytes,
        bytesPerRow: rowBytes,
        from: region,
        mipmapLevel: 0
    )
    
    let alphaInfo = CGImageAlphaInfo.premultipliedFirst
    let alphaBitmapInfo = CGBitmapInfo(rawValue: alphaInfo.rawValue)
    let finalBitmapInfo = alphaBitmapInfo.union(.byteOrder32Little)
    
    guard let provider = CGDataProvider(data: NSData(bytes: &bgraBytes, length: bgraBytes.count)) else {
        return nil
    }
    
    guard let colorSpace = CGColorSpace(name: CGColorSpace.sRGB) else {
        return nil
    }

    return CGImage(
        width: width,
        height: height,
        bitsPerComponent: 8,
        bitsPerPixel: 32,
        bytesPerRow: rowBytes,
        space: colorSpace,
        bitmapInfo: finalBitmapInfo,
        provider: provider,
        decode: nil,
        shouldInterpolate: false,
        intent: .defaultIntent
    )
}


// Convert CGImage -> MTLTexture
func cgImageToMTLTexture(_ cgImage: CGImage) -> MTLTexture? {
    let width = cgImage.width
    let height = cgImage.height
    
    guard let cmdQueue = commandQueue else { return nil }
    guard let cmdBuf = cmdQueue.makeCommandBuffer() else { return nil }

    let desc = MTLTextureDescriptor.texture2DDescriptor(
        pixelFormat: .bgra8Unorm_srgb,
        width: width,
        height: height,
        mipmapped: false
    )
    desc.usage = [.shaderRead, .renderTarget]
    
    guard let newTex = mtlDevice.makeTexture(descriptor: desc) else { return nil }
    
    let rowBytes = width * 4
    var bgraBytes = [UInt8](repeating: 0, count: rowBytes * height)
    
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    let context = CGContext(
        data: &bgraBytes,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: rowBytes,
        space: colorSpace,
        bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue
    )
    context?.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
    
    newTex.replace(
        region: MTLRegionMake2D(0, 0, width, height),
        mipmapLevel: 0,
        withBytes: &bgraBytes,
        bytesPerRow: rowBytes
    )
    cmdBuf.commit()
    cmdBuf.waitUntilCompleted()
    
    return newTex
}


// MARK: - extension CVPixelBuffer

extension CVPixelBuffer {
    public func toBGRA() throws -> CVPixelBuffer? {
        let pixFormat = CVPixelBufferGetPixelFormatType(self)
        guard pixFormat == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange else {
            return self
        }
        
        let yPlane = self.with { VImage(pixelBuffer: $0, plane: 0) }
        let cbcrPlane = self.with { VImage(pixelBuffer: $0, plane: 1) }
        guard let yImg = yPlane, let cbcrImg = cbcrPlane else { return nil }
        
        guard let outPB = CVPixelBuffer.make(width: yImg.width, height: yImg.height, format: kCVPixelFormatType_32BGRA) else {
            return nil
        }
        
        var argbImage = outPB.with { VImage(pixelBuffer: $0) }!
        try argbImage.draw(yBuffer: yImg.buffer, cbcrBuffer: cbcrImg.buffer)
        argbImage.permute(channelMap: [3, 2, 1, 0]) // ARGB -> BGRA
        return outPB
    }
    
    func with<T>(_ closure: (CVPixelBuffer) -> T) -> T {
        CVPixelBufferLockBaseAddress(self, .readOnly)
        let result = closure(self)
        CVPixelBufferUnlockBaseAddress(self, .readOnly)
        return result
    }
    
    static func make(width: Int, height: Int, format: OSType) -> CVPixelBuffer? {
        var pb: CVPixelBuffer?
        let attrs: [String: Any] = [
            kCVPixelBufferIOSurfacePropertiesKey as String: [:]
        ]
        CVPixelBufferCreate(
            kCFAllocatorDefault,
            width, height,
            format,
            attrs as CFDictionary,
            &pb
        )
        return pb
    }
}

struct VImage {
    let width: Int
    let height: Int
    let bytesPerRow: Int
    var buffer: vImage_Buffer
    
    init?(pixelBuffer: CVPixelBuffer, plane: Int) {
        guard let base = CVPixelBufferGetBaseAddressOfPlane(pixelBuffer, plane) else { return nil }
        self.width = CVPixelBufferGetWidthOfPlane(pixelBuffer, plane)
        self.height = CVPixelBufferGetHeightOfPlane(pixelBuffer, plane)
        self.bytesPerRow = CVPixelBufferGetBytesPerRowOfPlane(pixelBuffer, plane)
        self.buffer = vImage_Buffer(
            data: base,
            height: vImagePixelCount(height),
            width: vImagePixelCount(width),
            rowBytes: bytesPerRow
        )
    }
    
    init?(pixelBuffer: CVPixelBuffer) {
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        self.width = CVPixelBufferGetWidth(pixelBuffer)
        self.height = CVPixelBufferGetHeight(pixelBuffer)
        self.bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        self.buffer = vImage_Buffer(
            data: base,
            height: vImagePixelCount(height),
            width: vImagePixelCount(width),
            rowBytes: bytesPerRow
        )
    }
    
    mutating func draw(yBuffer: vImage_Buffer, cbcrBuffer: vImage_Buffer) throws {
        var y = yBuffer
        var cbcr = cbcrBuffer
        var matrix = vImage_YpCbCrToARGB()
        var pixelRange = vImage_YpCbCrPixelRange(
            Yp_bias: 0,
            CbCr_bias: 128,
            YpRangeMax: 255,
            CbCrRangeMax: 255,
            YpMax: 255,
            YpMin: 1,
            CbCrMax: 255,
            CbCrMin: 0
        )
        
        vImageConvert_YpCbCrToARGB_GenerateConversion(
            kvImage_YpCbCrToARGBMatrix_ITU_R_709_2,
            &pixelRange,
            &matrix,
            kvImage420Yp8_CbCr8,
            kvImageARGB8888,
            vImage_Flags(kvImageNoFlags)
        )
        
        let err = vImageConvert_420Yp8_CbCr8ToARGB8888(
            &y, &cbcr, &self.buffer, &matrix,
            nil, 255, vImage_Flags(kvImageNoFlags)
        )
        if err != kvImageNoError {
            throw NSError(domain: "vImageConvert", code: Int(err), userInfo: nil)
        }
    }
    
    mutating func permute(channelMap: [UInt8]) {
        vImagePermuteChannels_ARGB8888(&self.buffer, &self.buffer, channelMap, 0)
    }
}
