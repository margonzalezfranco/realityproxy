import ARKit
import RealityKit
import MetalKit
import Accelerate
import Foundation

// MARK: - Global Variables

// The ARKitSession that handles main camera frames
var arKitSession = ARKitSession()

// Flag to indicate if we're currently capturing
var isRunning = false

// Metal objects
let mtlDevice: MTLDevice = MTLCreateSystemDefaultDevice()!
var commandQueue: MTLCommandQueue!
var textureCache: CVMetalTextureCache!

// We'll store a single MTLTexture to reuse each frame
var currentTexture: MTLTexture? = nil

// This pointer is exposed to Unity
var pointer: UnsafeMutableRawPointer? = nil

// (NEW) We'll store the chosen resolution for Unity to query
var chosenCameraWidth: Int32 = 1920
var chosenCameraHeight: Int32 = 1080

// Add these new global variables to store the matrices
var currentIntrinsics: simd_float3x3 = .init()
var currentExtrinsics: simd_float4x4 = .init()

// MARK: - C-Style Exported Functions

/// Starts the camera capture loop asynchronously.
@_cdecl("startCapture")
public func startCapture() {
    print("startCapture() called.")
    isRunning = true
    
    Task {
        // 1) Find supported formats for the left main camera
        let formats = CameraVideoFormat.supportedVideoFormats(for: .main, cameraPositions: [.left])
        formats.forEach { fmt in
            let size = fmt.frameSize
            print("Supported format: \(size.width)x\(size.height)")
        }
        guard !formats.isEmpty else {
            print("No camera formats found for main left camera.")
            return
        }
        
        // Option A: pick the first (lowest resolution):
        // let firstFormat = formats.first!
        
        // Option B: pick the highest resolution:
        let firstFormat = formats.max { $0.frameSize.height < $1.frameSize.height }!
        
        // Store the chosen width/height so Unity can later retrieve them
        chosenCameraWidth = Int32(firstFormat.frameSize.width)
        chosenCameraHeight = Int32(firstFormat.frameSize.height)
        print("Chosen camera format: \(chosenCameraWidth)x\(chosenCameraHeight)")

        // 2) Request camera access from ARKitSession
        let statuses = await arKitSession.queryAuthorization(for: [.cameraAccess])
        // if statuses[.cameraAccess] != .authorized { ... }

        // 3) Create and run the CameraFrameProvider
        let cameraProvider = CameraFrameProvider()
        do {
            try await arKitSession.run([cameraProvider])
        } catch {
            print("Failed to run ARKitSession:", error)
            return
        }
        
        print("ARKit session running. Beginning camera capture loop...")

        // 4) Listen for camera frames in an async for-await loop
        guard let updates = cameraProvider.cameraFrameUpdates(for: firstFormat) else {
            print("No cameraFrameUpdates available for the chosen format.")
            return
        }
        
        for await frame in updates {
            if !isRunning { break }

            // Each frame has a pixel buffer (CVPixelBuffer) in YUV format
            let pixelBuffer = frame.primarySample.pixelBuffer
            let parameters = frame.primarySample.parameters
            
            // Store the current matrices
            currentIntrinsics = parameters.intrinsics
            currentExtrinsics = parameters.extrinsics
            
            // print(
            //     """
            //     Camera Position: \(parameters.cameraPosition) // Camera Position: left
            //     Camera Type: \(parameters.cameraType) // Camera Type: main
            //     Capture Timestamp: \(parameters.captureTimestamp) // Capture Timestamp: 7205.643720833334
            //     Color Temperature: \(parameters.colorTemperature)K // Color Temperature: 4526K
            //     Exposure Duration: \(parameters.exposureDuration)s // Exposure Duration: 0.006211s
            //     Extrinsics Matrix: \(parameters.extrinsics) // Extrinsics Matrix: simd_float4x4([[0.99122864, 0.006838121, -0.13198134, 0.0], [-0.0038917887, -0.99671704, -0.08086995, 0.0], [-0.13210104, 0.08067425, -0.9879479, 0.0], [0.024276158, -0.02069169, -0.057551354, 1.0]])
            //     Intrinsics Matrix: \(parameters.intrinsics) // Intrinsics Matrix: simd_float3x3([[736.6339, 0.0, 960.0], [0.0, 736.6339, 540.0], [0.0, 0.0, 1.0]])
            //     Mid Exposure Time: \(parameters.midExposureTimestamp) // Mid Exposure Time: 10326.427177458332
            //     """)

            // Convert to BGRA and create (or update) our MTLTexture
            createTexture(from: pixelBuffer)
        }
        
        print("Camera capture loop finished.")
    }
}

/// Stops the camera capture and ARKit session.
@_cdecl("stopCapture")
public func stopCapture() {
    print("stopCapture() called.")
    isRunning = false
    arKitSession.stop()
    print("ARKit session stopped.")
}

/// Returns the pointer to our current MTLTexture, or nil if not ready.
@_cdecl("getTexturePointer")
public func getTexturePointer() -> UnsafeMutableRawPointer? {
    return pointer
}

// Let Unity retrieve the chosen resolution
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

// MARK: - Main Camera → MTLTexture Creation

/// Converts from YUV to BGRA, creates (or updates) a Metal texture, then sets `pointer`.
func createTexture(from pixelBuffer: CVPixelBuffer) {
    // 1) Convert from YUV (kCVPixelFormatType_420YpCbCr8BiPlanarFullRange) to BGRA
    guard let bgraBuffer = try? pixelBuffer.toBGRA() else {
        // If conversion fails, skip
        return
    }
    
    let width = CVPixelBufferGetWidth(bgraBuffer)
    let height = CVPixelBufferGetHeight(bgraBuffer)
    
    // 2) Lazy-initialize our texture cache and command queue
    if textureCache == nil {
        CVMetalTextureCacheCreate(kCFAllocatorDefault, nil, mtlDevice, nil, &textureCache)
    }
    if commandQueue == nil {
        commandQueue = mtlDevice.makeCommandQueue()
    }
    
    // 3) Create a CVMetalTexture from the BGRA pixel buffer
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
        print("CVMetalTextureCacheCreateTextureFromImage failed with code:", creationResult)
        return
    }
    
    // 4) Allocate our reusable MTLTexture if needed
    if currentTexture == nil {
        let desc = MTLTextureDescriptor.texture2DDescriptor(
            pixelFormat: sourceTexture.pixelFormat,
            width: sourceTexture.width,
            height: sourceTexture.height,
            mipmapped: false
        )
        // On iOS/visionOS, typically usage is just .shaderRead if we're sampling it
        desc.usage = [.shaderRead]
        
        currentTexture = mtlDevice.makeTexture(descriptor: desc)
    }
    
    // 5) Copy from the sourceTexture into our persistent currentTexture using a blit command
    guard let finalTex = currentTexture,
          let cmdBuf = commandQueue?.makeCommandBuffer(),
          let blitEnc = cmdBuf.makeBlitCommandEncoder()
    else {
        return
    }
    
    blitEnc.copy(
        from: sourceTexture,
        sourceSlice: 0, sourceLevel: 0,
        sourceOrigin: MTLOrigin(x: 0, y: 0, z: 0),
        sourceSize: MTLSize(width: sourceTexture.width, height: sourceTexture.height, depth: 1),
        to: finalTex,
        destinationSlice: 0, destinationLevel: 0,
        destinationOrigin: MTLOrigin(x: 0, y: 0, z: 0)
    )
    blitEnc.endEncoding()
    cmdBuf.commit()
    cmdBuf.waitUntilCompleted()
    
    // 6) Expose finalTex as an opaque pointer for Unity
    if pointer == nil {
        pointer = Unmanaged.passUnretained(finalTex).toOpaque()
    }
}

// MARK: - YUV -> BGRA Conversion (vImage)

extension CVPixelBuffer {
    /// Converts from YUV (kCVPixelFormatType_420YpCbCr8BiPlanarFullRange) to BGRA.
    /// If already BGRA, returns self.
    public func toBGRA() throws -> CVPixelBuffer? {
        let pixFormat = CVPixelBufferGetPixelFormatType(self)
        // If it's already BGRA, skip conversion
        guard pixFormat == kCVPixelFormatType_420YpCbCr8BiPlanarFullRange else {
            return self
        }
        
        let yPlane = self.with { VImage(pixelBuffer: $0, plane: 0) }
        let cbcrPlane = self.with { VImage(pixelBuffer: $0, plane: 1) }
        guard let yImg = yPlane, let cbcrImg = cbcrPlane else {
            return nil
        }
        
        guard let outPB = CVPixelBuffer.make(width: yImg.width, height: yImg.height, format: kCVPixelFormatType_32BGRA) else {
            return nil
        }
        
        var argbImage = outPB.with { VImage(pixelBuffer: $0) }!
        try argbImage.draw(yBuffer: yImg.buffer, cbcrBuffer: cbcrImg.buffer)
        
        // ARGB -> BGRA
        argbImage.permute(channelMap: [3, 2, 1, 0])
        
        return outPB
    }
    
    /// Lock/unlock base address for a closure call
    func with<T>(_ closure: (CVPixelBuffer) -> T) -> T {
        CVPixelBufferLockBaseAddress(self, .readOnly)
        let result = closure(self)
        CVPixelBufferUnlockBaseAddress(self, .readOnly)
        return result
    }
    
    /// Creates a pixel buffer with iOSurface usage
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
        
        // Use ITU_R_709_2 for color conversion
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
        // Reorder ARGB to BGRA
        vImagePermuteChannels_ARGB8888(&self.buffer, &self.buffer, channelMap, 0)
    }
}
