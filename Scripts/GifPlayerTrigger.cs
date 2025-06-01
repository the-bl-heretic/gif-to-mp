using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Attach this to a Quad (with an Unlit/Transparent or UI/Default material) or a UI RawImage.
/// Assign a GIF as a TextAsset (e.g. "myAnimation.gif.bytes") in the Inspector.
/// Then call PlayGif() (e.g. via UltEvents) to start decoding and playing frame‐by‐frame.
/// 
/// This version fixes several issues:
///  1. Proper loop‐count logic (honors the NETSCAPE extension).
///  2. Correct disposal‐method handling (restore‐to‐background and restore‐to‐previous).
///  3. Compositing each frame onto a persistent "canvas" Texture2D, avoiding the need to Destroy/Create every frame.
///  4. Raw‐byte peeking (instead of BinaryReader.PeekChar) to avoid misinterpreting binary data as text.
///  5. Minimum‐delay clamping so that "0" hundredths‐of‐a‐second still yields a small pause (e.g. 20 ms).
///  6. Background‐color support from the logical screen descriptor when disposal says "restore to background".
///  7. More robust error handling on color tables and sub‐blocks to guard against malformed or truncated GIFs.
///  8. Option to force an infinite loop or respect the GIF's internal loop count.
/// </summary>
public class GifPlayerTrigger : MonoBehaviour
{
    [Header("Drag & Drop GIF as TextAsset")]
    [Tooltip("Drag your .gif‐loaded‐as‐TextAsset here (e.g. foo.gif.bytes).")]
    public TextAsset gifAsset;

    [Tooltip("If true, automatically start playing on Awake.")]
    public bool playOnAwake = false;

    [Tooltip("If true, force loop forever (ignoring the GIF's internal loop count).")]
    public bool forceLoopForever = false;

    [Tooltip("Minimum frame delay in seconds (clamps any 0‐delay to this).")]
    public float minimumFrameDelay = 0.02f;

    private Renderer _renderer;         // For 3D quads/planes
    private RawImage _rawImage;         // For UI RawImage
    private Material _materialClone;    // We’ll clone so we don’t overwrite a shared material

    private Texture2D _canvasTexture;   // The full logical screen canvas (width × height)
    private Color32[] _canvasPixels;    // Pixel buffer for the entire canvas
    private Color32[] _previousCanvas;  // Backup buffer when disposalMethod == 3

    private Coroutine _playCoroutine;

    private void Awake()
    {
        _renderer = GetComponent<Renderer>();
        _rawImage = GetComponent<RawImage>();

        if (_renderer == null && _rawImage == null)
        {
            UnityEngine.Debug.LogError("[GifPlayerTrigger] Requires either a Renderer or a RawImage on this GameObject.");
            enabled = false;
            return;
        }

        if (playOnAwake)
            PlayGif();
    }

    /// <summary>
    /// Starts decoding and playing the GIF. Stops any existing playback first.
    /// </summary>
    public void PlayGif()
    {
        if (gifAsset == null)
        {
            UnityEngine.Debug.LogError("[GifPlayerTrigger] No GIF TextAsset assigned!");
            return;
        }

        if (_playCoroutine != null)
        {
            StopCoroutine(_playCoroutine);
            CleanupCurrentResources();
        }

        _playCoroutine = StartCoroutine(StreamAndPlayGif());
    }

    /// <summary>
    /// Stops playback and clears the last displayed frame.
    /// </summary>
    public void StopGif()
    {
        if (_playCoroutine != null)
        {
            StopCoroutine(_playCoroutine);
            _playCoroutine = null;
        }
        CleanupCurrentResources();
    }

    /// <summary>
    /// Cleans up allocated textures/materials when stopping or destroying.
    /// </summary>
    private void CleanupCurrentResources()
    {
        if (_canvasTexture != null)
        {
            Destroy(_canvasTexture);
            _canvasTexture = null;
        }
        if (_materialClone != null)
        {
            Destroy(_materialClone);
            _materialClone = null;
        }
    }

    private void OnDestroy()
    {
        CleanupCurrentResources();
    }

    /// <summary>
    /// Core coroutine: reads header & global palette, then decodes and composites frames one by one.
    /// </summary>
    private IEnumerator StreamAndPlayGif()
    {
        byte[] data = gifAsset.bytes;

        int loopCountFromGif = 1;    // 1 means “play once”; 0 means “infinite”
        int loopsToDo = 1;           // We'll reset this once we read the NETSCAPE extension.

        // State variables for disposal and composition:
        int prevDisposal = 0;
        int prevImgLeft = 0, prevImgTop = 0, prevImgWidth = 0, prevImgHeight = 0;

        using (MemoryStream ms = new MemoryStream(data))
        using (BinaryReader reader = new BinaryReader(ms))
        {
            // --- 1. Read GIF Header ---
            string header = System.Text.Encoding.ASCII.GetString(reader.ReadBytes(6));
            if (header != "GIF87a" && header != "GIF89a")
            {
                UnityEngine.Debug.LogError("[GifPlayerTrigger] Not a valid GIF.");
                yield break;
            }

            // --- 2. Logical Screen Descriptor ---
            int screenWidth = reader.ReadUInt16();
            int screenHeight = reader.ReadUInt16();
            byte packed = reader.ReadByte();
            bool hasGlobalCT = (packed & 0x80) != 0;
            int gctSize = 2 << (packed & 0x07);

            byte bgColorIndex = reader.ReadByte();
            reader.ReadByte(); // pixelAspect (ignored)

            // Read Global Color Table if present
            Color32[] globalColorTable = null;
            if (hasGlobalCT)
            {
                globalColorTable = ReadColorTableSafe(reader, gctSize);
                if (globalColorTable == null)
                {
                    UnityEngine.Debug.LogError("[GifPlayerTrigger] Failed to read Global Color Table.");
                    yield break;
                }
            }

            // Determine the GIF's “background color” from the Global CT (or transparent if none)
            Color32 backgroundColor = new Color32(0, 0, 0, 0);
            if (hasGlobalCT && bgColorIndex < globalColorTable.Length)
            {
                backgroundColor = globalColorTable[bgColorIndex];
                backgroundColor.a = 255;
            }

            // --- 3. Prepare a persistent “canvas” for full‐screen frames ---
            _canvasTexture = new Texture2D(screenWidth, screenHeight, TextureFormat.RGBA32, false);
            _canvasTexture.filterMode = FilterMode.Point;
            _canvasTexture.wrapMode = TextureWrapMode.Clamp;
            _canvasPixels = new Color32[screenWidth * screenHeight];

            // Initialize the canvas to the background color (or fully transparent if no global CT)
            for (int i = 0; i < _canvasPixels.Length; i++)
            {
                _canvasPixels[i] = backgroundColor;
            }
            _canvasTexture.SetPixels32(_canvasPixels);
            _canvasTexture.Apply();

            // Assign the canvas texture to the Renderer or RawImage
            if (_renderer != null)
            {
                // Clone the material once so we don’t overwrite a shared asset
                _materialClone = new Material(_renderer.sharedMaterial);
                _renderer.material = _materialClone;
                _materialClone.mainTexture = _canvasTexture;
            }
            else if (_rawImage != null)
            {
                // Use Unity's built‐in UI/Default shader (supports transparency)
                Shader uiShader = Shader.Find("UI/Default");
                if (uiShader == null)
                {
                    UnityEngine.Debug.LogWarning("[GifPlayerTrigger] Could not find 'UI/Default' shader. RawImage might not render transparency correctly.");
                }
                else
                {
                    Material mat = new Material(uiShader);
                    mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                    mat.SetInt("_ZWrite", 0);
                    mat.DisableKeyword("_ALPHATEST_ON");
                    mat.EnableKeyword("_ALPHABLEND_ON");
                    mat.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                    mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
                    _rawImage.material = mat;
                    _materialClone = mat; // so CleanupCurrentResources can destroy it later
                }
                _rawImage.texture = _canvasTexture;
            }

            // Variables that hold the “pending” Graphic Control Extension (GCE) info for each upcoming frame:
            int currDisposal = 1;           // 0/1 = no disposal specified (i.e. leave as‐is)
            bool currHasTransparency = false;
            int currTransparencyIndex = 0;
            int currDelayHundredths = 0;

            // We'll read through all blocks once, but loop based on loopsToDo:
            bool reachedTrailer = false;

            // First pass: we need to read the NETSCAPE Loop Count if present before deciding loopsToDo.
            // To do that, we scan ahead from the current stream position—stashing the loops if we see it.
            long preReadPos = ms.Position;
            {
                int tempLoopCount = 1;
                while (ms.Position < ms.Length)
                {
                    int id = PeekByteSafe(ms);
                    if (id < 0) break;

                    if (id == 0x21) // Extension
                    {
                        reader.ReadByte(); // consume 0x21
                        int label = reader.ReadByte();
                        if (label == 0xFF)
                        {
                            // Application Extension block
                            int blockSize = reader.ReadByte();
                            byte[] appIdBytes = reader.ReadBytes(blockSize);
                            string appId = System.Text.Encoding.ASCII.GetString(appIdBytes);
                            if (appId.StartsWith("NETSCAPE"))
                            {
                                // Expect a sub‐block of size 3: [subID=1][loops (UInt16)][0]
                                int subSize = reader.ReadByte(); // usually 3
                                if (subSize >= 3)
                                {
                                    int subID = reader.ReadByte(); // usually 1
                                    ushort loops = reader.ReadUInt16();
                                    tempLoopCount = loops; // 0 means infinite
                                    reader.ReadByte();     // terminator (0)
                                }
                                else
                                {
                                    SkipSubBlocks(reader);
                                }
                            }
                            else
                            {
                                SkipSubBlocks(reader);
                            }
                        }
                        else
                        {
                            // skip any other extension
                            SkipSubBlocks(reader);
                        }
                    }
                    else if (id == 0x3B) // Trailer
                    {
                        reader.ReadByte(); // consume 0x3B
                        break;
                    }
                    else if (id == 0x2C) // Image Descriptor
                    {
                        // Skip the image descriptor + its data to jump past this frame
                        reader.ReadByte(); // consume 0x2C
                        reader.ReadUInt16(); // left
                        reader.ReadUInt16(); // top
                        reader.ReadUInt16(); // width
                        reader.ReadUInt16(); // height
                        byte imgPacked = reader.ReadByte();
                        bool hasLCT = (imgPacked & 0x80) != 0;
                        int lctSize = 2 << (imgPacked & 0x07);
                        if (hasLCT)
                        {
                            // skip local color table
                            reader.ReadBytes(lctSize * 3);
                        }
                        // skip LZW‐encoded data subblocks
                        SkipDataSubBlocks(reader);
                    }
                    else
                    {
                        // Unknown/garbage: advance one byte and continue
                        reader.ReadByte();
                    }
                }

                // Now we know the loop count (default=1 if none found)
                loopCountFromGif = tempLoopCount;
                loopsToDo = (loopCountFromGif == 0 ? 1 : loopCountFromGif);

                // Reset stream position so we can actually decode frames
                ms.Position = preReadPos;
            }

            // --- 4. Main loop: decode frames and composite onto the canvas ---
            do
            {
                prevDisposal = 1; // default for “no disposal”
                prevImgLeft = prevImgTop = prevImgWidth = prevImgHeight = 0;
                _previousCanvas = null;

                // Rewind to just after the Logical Screen Descriptor (i.e. right where we set preReadPos)
                ms.Position = preReadPos;

                bool doneReading = false;
                while (!doneReading && ms.Position < ms.Length)
                {
                    int blockID = PeekByteSafe(ms);
                    if (blockID < 0)
                    {
                        // Reached unexpected end of stream
                        doneReading = true;
                        break;
                    }

                    switch (blockID)
                    {
                        case 0x21: // Extension
                            reader.ReadByte();           // consume 0x21
                            int label = reader.ReadByte();
                            if (label == 0xF9)
                            {
                                // --- Graphics Control Extension (GCE) ---
                                int blockSize = reader.ReadByte(); // should be 4
                                byte gcePacked = reader.ReadByte();
                                currDisposal = (gcePacked >> 2) & 0x07;
                                currHasTransparency = (gcePacked & 0x01) != 0;
                                currDelayHundredths = reader.ReadUInt16();
                                currTransparencyIndex = reader.ReadByte();
                                reader.ReadByte(); // terminator (0)
                            }
                            else if (label == 0xFF)
                            {
                                // --- Application Extension (NETSCAPE Loop Count) ---
                                int appBlockSize = reader.ReadByte(); // usually 11
                                byte[] appIdBytes = reader.ReadBytes(appBlockSize);
                                string appId = System.Text.Encoding.ASCII.GetString(appIdBytes);
                                if (appId.StartsWith("NETSCAPE"))
                                {
                                    int subSize = reader.ReadByte(); // usually 3
                                    int subID = reader.ReadByte();   // usually 1
                                    ushort loops = reader.ReadUInt16();
                                    loopCountFromGif = loops;    // 0 means infinite
                                    loopsToDo = (loopCountFromGif == 0 ? 1 : loopCountFromGif);
                                    reader.ReadByte(); // terminator
                                }
                                else
                                {
                                    SkipSubBlocks(reader);
                                }
                            }
                            else
                            {
                                // Other extension – skip its sub‐blocks
                                SkipSubBlocks(reader);
                            }
                            break;

                        case 0x2C: // Image Descriptor → decode one frame
                            reader.ReadByte(); // consume 0x2C

                            // Read the frame’s position & size
                            int imgLeft = reader.ReadUInt16();
                            int imgTop = reader.ReadUInt16();
                            int imgWidth = reader.ReadUInt16();
                            int imgHeight = reader.ReadUInt16();
                            byte imgPacked2 = reader.ReadByte();
                            bool hasLocalCT = (imgPacked2 & 0x80) != 0;
                            bool isInterlaced = (imgPacked2 & 0x40) != 0;
                            int lctSize = 2 << (imgPacked2 & 0x07);

                            // Read (or skip) the Local Color Table
                            Color32[] localColorTable = null;
                            if (hasLocalCT)
                            {
                                localColorTable = ReadColorTableSafe(reader, lctSize);
                                if (localColorTable == null)
                                {
                                    UnityEngine.Debug.LogError("[GifPlayerTrigger] Failed to read Local Color Table for a frame.");
                                    yield break;
                                }
                            }

                            // Choose which palette to use (local overrides global)
                            Color32[] activePalette = hasLocalCT ? localColorTable : globalColorTable;
                            if (activePalette == null)
                            {
                                UnityEngine.Debug.LogError("[GifPlayerTrigger] No color table available for this frame!");
                                yield break;
                            }

                            // --- 4a. Handle disposal of the previous frame prior to drawing this one ---
                            if (prevDisposal == 2)
                            {
                                // Restore the rectangle area to the background color
                                for (int y = prevImgTop; y < prevImgTop + prevImgHeight; y++)
                                {
                                    for (int x = prevImgLeft; x < prevImgLeft + prevImgWidth; x++)
                                    {
                                        int idx = y * screenWidth + x;
                                        _canvasPixels[idx] = backgroundColor;
                                    }
                                }
                            }
                            else if (prevDisposal == 3 && _previousCanvas != null)
                            {
                                // Restore entire canvas from the backup we took before drawing the previous frame
                                Array.Copy(_previousCanvas, _canvasPixels, _canvasPixels.Length);
                            }
                            // If prevDisposal is 0 or 1, we do nothing (leave the canvas as is)

                            // --- 4b. If the upcoming frame’s disposal == 3, we need to snapshot the canvas now ---
                            if (currDisposal == 3)
                            {
                                if (_previousCanvas == null || _previousCanvas.Length != _canvasPixels.Length)
                                    _previousCanvas = new Color32[_canvasPixels.Length];
                                Array.Copy(_canvasPixels, _previousCanvas, _canvasPixels.Length);
                            }

                            // --- 4c. Decode LZW‐compressed indices for this frame ---
                            int lzwMinCodeSize = reader.ReadByte();
                            byte[] compressedData = ReadDataSubBlocks(reader);
                            if (compressedData == null)
                            {
                                UnityEngine.Debug.LogError("[GifPlayerTrigger] Unexpected end of data while reading frame’s LZW subblocks.");
                                yield break;
                            }

                            byte[] pixelIndices = LzwDecodeOneFrameSafe(
                                compressedData,
                                lzwMinCodeSize,
                                imgWidth,
                                imgHeight,
                                isInterlaced
                            );
                            if (pixelIndices == null || pixelIndices.Length < imgWidth * imgHeight)
                            {
                                UnityEngine.Debug.LogError("[GifPlayerTrigger] LZW decoded fewer pixels than expected for a frame.");
                                yield break;
                            }

                            // --- 4d. Composite this frame’s pixels onto the canvas at (imgLeft, imgTop) ---
                            for (int row = 0; row < imgHeight; row++)
                            {
                                int destRow = imgTop + row;
                                if (destRow < 0 || destRow >= screenHeight) continue; // out of bounds guard

                                for (int col = 0; col < imgWidth; col++)
                                {
                                    int destCol = imgLeft + col;
                                    if (destCol < 0 || destCol >= screenWidth) continue;

                                    int srcIndex = row * imgWidth + col;
                                    byte colorIndex = pixelIndices[srcIndex];

                                    if (currHasTransparency && colorIndex == currTransparencyIndex)
                                    {
                                        // Skip—leave whatever was on the canvas already
                                        continue;
                                    }
                                    else
                                    {
                                        Color32 c = activePalette[colorIndex];
                                        c.a = 255;
                                        int destIndex = destRow * screenWidth + destCol;
                                        _canvasPixels[destIndex] = c;
                                    }
                                }
                            }

                            // --- 4e. Upload the updated canvas to the GPU ---
                            _canvasTexture.SetPixels32(_canvasPixels);
                            _canvasTexture.Apply();

                            // --- 4f. Wait for this frame’s delay (clamped to minimumFrameDelay) ---
                            float delaySec = Mathf.Max(currDelayHundredths / 100f, minimumFrameDelay);
                            yield return new WaitForSeconds(delaySec);

                            // --- 4g. Prepare for next iteration’s disposal step ---
                            prevDisposal = currDisposal;
                            prevImgLeft = imgLeft;
                            prevImgTop = imgTop;
                            prevImgWidth = imgWidth;
                            prevImgHeight = imgHeight;

                            // Reset the “pending” GCE values so that if no new GCE appears, 
                            // the next frame will default to no transparency & no disposal change.
                            currDisposal = 1;
                            currHasTransparency = false;
                            currTransparencyIndex = 0;
                            currDelayHundredths = 0;
                            break;

                        case 0x3B: // Trailer → end of GIF stream
                            doneReading = true;
                            reader.ReadByte(); // consume 0x3B
                            break;

                        default:
                            // Unknown block: skip subblocks just in case
                            SkipSubBlocks(reader);
                            break;
                    }
                } // end while (!doneReading)

                // After we finish one full pass (all frames), decrement loopsToDo.
                loopsToDo--;
            }
            while ((forceLoopForever) || (loopCountFromGif == 0) || (loopsToDo > 0));
        } // end using MemoryStream & BinaryReader

        // Once the loop ends, clear everything (leave a blank canvas or background color)
        for (int i = 0; i < _canvasPixels.Length; i++)
            _canvasPixels[i] = new Color32(0, 0, 0, 0);
        _canvasTexture.SetPixels32(_canvasPixels);
        _canvasTexture.Apply();
    }

    #region ─── Helper Methods ───────────────────────────────────────────────────

    /// <summary>
    /// Safely reads a color table of “size” entries (each entry = 3 bytes). Returns null if truncated.
    /// </summary>
    private Color32[] ReadColorTableSafe(BinaryReader reader, int size)
    {
        int byteCount = size * 3;
        byte[] raw = reader.ReadBytes(byteCount);
        if (raw.Length < byteCount)
            return null;

        Color32[] table = new Color32[size];
        for (int i = 0; i < size; i++)
        {
            int idx = i * 3;
            table[i] = new Color32(raw[idx], raw[idx + 1], raw[idx + 2], 255);
        }
        return table;
    }

    /// <summary>
    /// Reads LZW‐encoded data subblocks until a zero‐length block is encountered.
    /// Returns concatenated data bytes, or null if the stream ended unexpectedly.
    /// </summary>
    private byte[] ReadDataSubBlocks(BinaryReader reader)
    {
        using (MemoryStream msOut = new MemoryStream())
        {
            try
            {
                byte blockSize = reader.ReadByte();
                while (blockSize > 0)
                {
                    byte[] chunk = reader.ReadBytes(blockSize);
                    if (chunk.Length < blockSize)
                        return null; // unexpected end

                    msOut.Write(chunk, 0, chunk.Length);
                    blockSize = reader.ReadByte();
                }
                return msOut.ToArray();
            }
            catch (EndOfStreamException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Performs LZW decoding for one frame. Returns an array of pixel indices (length = imgWidth × imgHeight),
    /// or null if an error occurred. Handles interlaced data if isInterlaced is true.
    /// </summary>
    private byte[] LzwDecodeOneFrameSafe(byte[] compressedData, int minCodeSize, int imgWidth, int imgHeight, bool isInterlaced)
    {
        int pixelCount = imgWidth * imgHeight;
        int clearCode = 1 << minCodeSize;
        int endCode = clearCode + 1;
        int codeSize = minCodeSize + 1;
        int nextCode = endCode + 1;

        // Initialize dictionary
        Dictionary<int, List<byte>> dict = new Dictionary<int, List<byte>>();
        int dictInitSize = 1 << minCodeSize;
        for (int i = 0; i < dictInitSize; i++)
        {
            dict[i] = new List<byte> { (byte)i };
        }
        dict[clearCode] = new List<byte>();
        dict[endCode] = new List<byte>();

        GifLzwBitReader bitReader = new GifLzwBitReader(compressedData);
        List<byte> output = new List<byte>(pixelCount);
        int prevCode = -1;

        try
        {
            while (true)
            {
                int code = bitReader.ReadBits(codeSize);
                if (code < 0) break;

                if (code == clearCode)
                {
                    codeSize = minCodeSize + 1;
                    nextCode = endCode + 1;
                    dict.Clear();
                    for (int i = 0; i < dictInitSize; i++)
                    {
                        dict[i] = new List<byte> { (byte)i };
                    }
                    dict[clearCode] = new List<byte>();
                    dict[endCode] = new List<byte>();
                    prevCode = -1;
                    continue;
                }
                else if (code == endCode)
                {
                    break;
                }

                List<byte> entry;
                if (dict.ContainsKey(code))
                {
                    entry = new List<byte>(dict[code]);
                }
                else if (code == nextCode && prevCode >= 0)
                {
                    List<byte> prevEntry = dict[prevCode];
                    entry = new List<byte>(prevEntry);
                    entry.Add(prevEntry[0]);
                }
                else
                {
                    // Invalid code → abort
                    return null;
                }

                output.AddRange(entry);

                if (prevCode >= 0)
                {
                    List<byte> newEntry = new List<byte>(dict[prevCode]);
                    newEntry.Add(entry[0]);
                    if (!dict.ContainsKey(nextCode))
                        dict[nextCode] = newEntry;
                    nextCode++;
                    if (nextCode == (1 << codeSize) && codeSize < 12)
                        codeSize++;
                }

                prevCode = code;
                if (output.Count >= pixelCount)
                    break;
            }

            if (output.Count < pixelCount)
                return null;
        }
        catch
        {
            return null;
        }

        byte[] linear = output.GetRange(0, pixelCount).ToArray();
        if (!isInterlaced)
            return linear;

        // Deinterlace
        byte[] deinterlaced = new byte[pixelCount];
        int[] passRowStart = new int[] { 0, 4, 2, 1 };
        int[] passInc = new int[] { 8, 8, 4, 2 };
        int srcIdx = 0;
        for (int pass = 0; pass < 4; pass++)
        {
            for (int row = passRowStart[pass]; row < imgHeight; row += passInc[pass])
            {
                int destBase = row * imgWidth;
                Array.Copy(linear, srcIdx, deinterlaced, destBase, imgWidth);
                srcIdx += imgWidth;
            }
        }
        return deinterlaced;
    }

    /// <summary>
    /// Safely skips over subblocks (size‐prefixed) until you reach a zero length. 
    /// </summary>
    private void SkipSubBlocks(BinaryReader reader)
    {
        try
        {
            byte len = reader.ReadByte();
            while (len > 0)
            {
                reader.BaseStream.Seek(len, SeekOrigin.Current);
                len = reader.ReadByte();
            }
        }
        catch (EndOfStreamException)
        {
            // truncated—just return
        }
    }

    /// <summary>
    /// Like SkipSubBlocks but specifically for LZW data, in case length might be zero immediately.
    /// </summary>
    private void SkipDataSubBlocks(BinaryReader reader)
    {
        try
        {
            byte blockSize = reader.ReadByte();
            while (blockSize > 0)
            {
                reader.BaseStream.Seek(blockSize, SeekOrigin.Current);
                blockSize = reader.ReadByte();
            }
        }
        catch (EndOfStreamException)
        {
            // truncated—just return
        }
    }

    /// <summary>
    /// Peeks a single raw byte from the MemoryStream without advancing the reader pointer.
    /// Returns -1 if at end of stream.
    /// </summary>
    private int PeekByteSafe(MemoryStream ms)
    {
        if (ms.Position >= ms.Length) return -1;
        int b = ms.ReadByte();
        ms.Position -= 1;
        return b;
    }

    /// <summary>
    /// Internal bit‐reader for LZW decoding (reads 'count' bits, least‐significant first).
    /// Returns -1 on end‐of‐data.
    /// </summary>
    private class GifLzwBitReader
    {
        private byte[] _data;
        private int _bytePos = 0;
        private int _bitPos = 0;

        public GifLzwBitReader(byte[] data)
        {
            _data = data;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                if (_bytePos >= _data.Length)
                    return -1;
                int bit = (_data[_bytePos] >> _bitPos) & 1;
                value |= (bit << i);
                _bitPos++;
                if (_bitPos == 8)
                {
                    _bitPos = 0;
                    _bytePos++;
                }
            }
            return value;
        }
    }

    #endregion
}
