import ARKit
import RealityKit
import MetalKit
import Accelerate
import Foundation
import Network
import CoreGraphics
import UIKit

// MARK: - Global Vars

var arKitSession = ARKitSession()
var isRunning = false

let mtlDevice: MTLDevice = MTLCreateSystemDefaultDevice()!
var commandQueue: MTLCommandQueue!
var textureCache: CVMetalTextureCache!

// The main camera feed texture
var currentTexture: MTLTexture? = nil
// The mask texture (object=255, background=0) from the child server
var maskTexture: MTLTexture? = nil

// Pointers for Unity
var pointerCam: UnsafeMutableRawPointer? = nil
var pointerMask: UnsafeMutableRawPointer? = nil

var chosenCameraWidth: Int32 = 1920
var chosenCameraHeight: Int32 = 1080

var currentIntrinsics: simd_float3x3 = .init()
var currentExtrinsics: simd_float4x4 = .init()

// TCP
var tcpConnection: NWConnection?
var latestMaskData: Data? = nil
let maskDataQueue = DispatchQueue(label: "maskData.queue")

// MARK: - C Fns

@_cdecl("startCapture")
public func startCapture() {
    print("startCapture() called.")
    isRunning = true
    
    connectToPythonServer(host: "192.168.1.84", port: 12345)  // change IP/port as needed
    
    Task {
        let formats = CameraVideoFormat.supportedVideoFormats(for: .main, cameraPositions: [.left])
        guard !formats.isEmpty else {
            print("No camera formats found for main left camera.")
            return
        }
        // pick highest
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
        
        print("ARKit session running. Starting capture loop...")

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
            
            // Send half-res to python
            sendFrameToServer(pixelBuffer: pixelBuffer)
            
            // Create camera texture for Unity
            createCameraTexture(from: pixelBuffer)
            
            // Also decode the latest mask data into maskTexture
            updateMaskTextureIfNeeded()
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
    tcpConnection?.cancel()
    tcpConnection = nil
}

@_cdecl("getTexturePointer")
public func getTexturePointer() -> UnsafeMutableRawPointer? {
    return pointerCam  // the color camera
}

@_cdecl("getMaskTexturePointer")
public func getMaskTexturePointer() -> UnsafeMutableRawPointer? {
    return pointerMask // the separate mask
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
    guard let nwPort = NWEndpoint.Port(rawValue: UInt16(port)) else { return }
    let tcpParams = NWParameters.tcp
    tcpConnection = NWConnection(host: NWEndpoint.Host(host), port: nwPort, using: tcpParams)
    
    tcpConnection?.stateUpdateHandler = { state in
        switch state {
        case .ready:
            print("[Swift] TCP connected to server.")
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
    // read length
    tcpConnection?.receive(minimumIncompleteLength: 4, maximumLength: 4) { data, _, _, error in
        if let e = error {
            print("Receive length error:", e)
            return
        }
        guard let lengthData = data, lengthData.count == 4 else {
            print("No length data.")
            return
        }
        let length = lengthData.withUnsafeBytes { $0.load(as: Int32.self).littleEndian }
        if length <= 0 {
            print("Invalid length:", length)
            return
        }
        tcpConnection?.receive(minimumIncompleteLength: Int(length), maximumLength: Int(length)) { content, _, _, err2 in
            if let e2 = err2 {
                print("Receive content error:", e2)
                return
            }
            guard let maskBytes = content, maskBytes.count == Int(length) else {
                print("Mask data mismatch.")
                return
            }
            maskDataQueue.async {
                latestMaskData = maskBytes
            }
            // read next
            startReadingFromServer()
        }
    }
}

func sendFrameToServer(pixelBuffer: CVPixelBuffer) {
    // half-res JPEG
    guard let jpegData = encodePixelBufferToHalfResJPEG(pixelBuffer: pixelBuffer, quality: 0.5) else { return }
    let length = Int32(jpegData.count).littleEndian
    var lengthData = withUnsafeBytes(of: length) { Data($0) }
    lengthData.append(jpegData)
    tcpConnection?.send(content: lengthData, completion: .contentProcessed({ error in
        if let e = error {
            print("Send error:", e)
        }
    }))
}

// half res
func encodePixelBufferToHalfResJPEG(pixelBuffer: CVPixelBuffer, quality: CGFloat) -> Data? {
    guard let cgImage = pixelBufferToCGImage(pixelBuffer) else { return nil }
    let uiImage = UIImage(cgImage: cgImage)
    let halfUIImage = scaleImage(uiImage, scale: 0.25)
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

// MARK: - Create main camera texture

func createCameraTexture(from pixelBuffer: CVPixelBuffer) {
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
    let result = CVMetalTextureCacheCreateTextureFromImage(
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
        result == kCVReturnSuccess,
        let metalTex = cvMetalTex,
        let sourceTex = CVMetalTextureGetTexture(metalTex)
    else {
        print("createCameraTexture: failed.")
        return
    }
    
    // Copy to currentTexture
    finalizeCameraCopy(inTex: sourceTex)
}

func finalizeCameraCopy(inTex: MTLTexture) {
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
    else { return }
    
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
    
    // store pointer
    if pointerCam == nil {
        pointerCam = Unmanaged.passUnretained(finalTex).toOpaque()
    }
}

// MARK: - Create mask texture separately

func updateMaskTextureIfNeeded() {
    // check if there's new mask data
    var newMaskData: Data? = nil
    maskDataQueue.sync {
        if let m = latestMaskData {
            newMaskData = m
            // We might clear latestMaskData to ensure we only decode once:
            // latestMaskData = nil
        }
    }
    guard let maskBytes = newMaskData else { return }
    // decode -> CGImage
    guard let cg = decodePNGToCGImage(maskBytes) else {
        print("Mask decode failed.")
        return
    }
    // create mask texture
    createMaskTexture(from: cg)
}

func createMaskTexture(from cg: CGImage) {
    let width = cg.width
    let height = cg.height

    // We want a BGRA8 texture holding grayscale
    if maskTexture == nil || maskTexture?.width != width || maskTexture?.height != height {
        let desc = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: .bgra8Unorm_srgb,
            width: width,
            height: height,
            mipmapped: false
        )
        desc.usage = [.shaderRead]
        maskTexture = mtlDevice.makeTexture(descriptor: desc)
    }
    guard let finalMaskTex = maskTexture else { return }

    // We'll copy the CGImage's grayscale into BGRA. 
    // For a minimal approach, we do: if pixel=255 => store BGRA=(255,255,255,255)
    
    let rowBytes = width * 4
    var bgraBytes = [UInt8](repeating: 0, count: rowBytes * height)
    
    // draw cg -> context
    let colorSpace = CGColorSpaceCreateDeviceRGB()
    guard let context = CGContext(
        data: &bgraBytes,
        width: width,
        height: height,
        bitsPerComponent: 8,
        bytesPerRow: rowBytes,
        space: colorSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue | CGBitmapInfo.byteOrder32Little.rawValue
    ) else {
        print("createMaskTexture: CG failed.")
        return
    }
    context.draw(cg, in: CGRect(x: 0, y: 0, width: width, height: height))

    // Now we have a BGRA8 image with R=G=B= your grayscale + alpha=255
    // Let's copy it to maskTexture
    guard let cmdBuf = commandQueue?.makeCommandBuffer(),
          let blitEnc = cmdBuf.makeBlitCommandEncoder() else {
        return
    }

    finalMaskTex.replace(
        region: MTLRegionMake2D(0, 0, width, height),
        mipmapLevel: 0,
        withBytes: &bgraBytes,
        bytesPerRow: rowBytes
    )
    blitEnc.endEncoding()
    cmdBuf.commit()
    cmdBuf.waitUntilCompleted()

    if pointerMask == nil {
        pointerMask = Unmanaged.passUnretained(finalMaskTex).toOpaque()
    }
}

func decodePNGToCGImage(_ data: Data) -> CGImage? {
    guard let uiImg = UIImage(data: data) else { return nil }
    return uiImg.cgImage
}

// same CVPixelBuffer extension

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
