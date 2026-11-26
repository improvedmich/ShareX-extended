#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using Microsoft.ML.OnnxRuntime;
using SkiaSharp;

namespace ShareX.OCR;

// Offline OCR with the PaddleOCR PP-OCRv6 models: a detection model finds the text lines and a recognition model
// reads each one. The recognition script selects the character set; the default model covers Latin and CJK, and the
// Cyrillic model covers Russian and the other Cyrillic languages.
public sealed class PaddleOCREngine : IDisposable
{
    // Models in the order they are preferred, the biggest and most accurate first. The small models ship with ShareX;
    // a user who downloads the medium models into the models folder gets them instead. The tiny models are used only
    // when the small ones have been removed. Detection and recognition are picked separately, so either one can be
    // replaced on its own.
    private static readonly string[] DetectionModelNames = ["PP-OCRv6_medium_det", "PP-OCRv6_small_det", "PP-OCRv6_tiny_det"];
    private static readonly string[] DefaultRecognitionModelNames = ["PP-OCRv6_medium_rec", "PP-OCRv6_small_rec", "PP-OCRv6_tiny_rec"];
    private static readonly string[] CyrillicRecognitionModelNames = ["PP-OCRv6_medium_rec_cyrillic", "PP-OCRv6_tiny_rec_cyrillic"];
    private const string BuiltInDetectionModelName = "PP-OCRv6_small_det";
    private const string BuiltInDefaultRecognitionModelName = "PP-OCRv6_small_rec";
    private const string BuiltInCyrillicRecognitionModelName = "PP-OCRv6_tiny_rec_cyrillic";

    // Hugging Face names the ONNX downloads with this suffix, so a folder copied from there as-is is found too.
    private const string DownloadFolderSuffix = "_onnx";

    // Recognized text below this confidence is usually an icon or noise read as characters.
    public const float DefaultMinScore = 0.5f;

    private static readonly Lock SharedLock = new();
    private static readonly Dictionary<OCRRecognitionScript, PaddleOCREngine> SharedEngines = [];
    // The detector does not depend on the script, so all engines for the same models folder share one.
    private static readonly Dictionary<string, TextDetector> SharedDetectors = new(StringComparer.OrdinalIgnoreCase);

    private readonly TextDetector _detector;
    private readonly TextRecognizer _recognizer;
    private readonly bool _ownsDetector;

    // The folders of the models in use.
    public string DetectionModelFolder { get; }
    public string RecognitionModelFolder { get; }

    // The models are copied next to the application under this folder.
    public static string DefaultModelsFolder => Path.Combine(AppContext.BaseDirectory, "Models");

    public static PaddleOCREngine Shared => GetShared(OCRRecognitionScript.Default);

    // Loading the models takes a moment, so one engine per script is kept for the life of the process. Detection is
    // independent of the script, so every script engine reuses the same detector session.
    public static PaddleOCREngine GetShared(OCRRecognitionScript script)
    {
        lock (SharedLock)
        {
            if (SharedEngines.TryGetValue(script, out PaddleOCREngine? engine))
            {
                return engine;
            }

            string detectionModelFolder = FindModelFolder(DefaultModelsFolder, DetectionModelNames, BuiltInDetectionModelName);
            string recognitionModelFolder = FindModelFolder(DefaultModelsFolder, GetRecognitionModelNames(script), GetBuiltInRecognitionModelName(script));
            TextDetector detector = GetSharedDetector(detectionModelFolder);

            engine = new PaddleOCREngine(detectionModelFolder, recognitionModelFolder, detector);
            SharedEngines[script] = engine;

            return engine;
        }
    }

    private static TextDetector GetSharedDetector(string detectionModelFolder)
    {
        if (SharedDetectors.TryGetValue(detectionModelFolder, out TextDetector? detector))
        {
            return detector;
        }

        using SessionOptions sessionOptions = CreateSessionOptions();
        detector = new TextDetector(detectionModelFolder, sessionOptions);
        SharedDetectors[detectionModelFolder] = detector;

        return detector;
    }

    public PaddleOCREngine(string modelsFolder) : this(modelsFolder, OCRRecognitionScript.Default)
    {
    }

    public PaddleOCREngine(string modelsFolder, OCRRecognitionScript script)
    {
        using SessionOptions sessionOptions = CreateSessionOptions();

        DetectionModelFolder = FindModelFolder(modelsFolder, DetectionModelNames, BuiltInDetectionModelName);
        RecognitionModelFolder = FindModelFolder(modelsFolder, GetRecognitionModelNames(script), GetBuiltInRecognitionModelName(script));
        _ownsDetector = true;
        _detector = new TextDetector(DetectionModelFolder, sessionOptions);

        try
        {
            _recognizer = new TextRecognizer(RecognitionModelFolder, sessionOptions);
        }
        catch
        {
            _detector.Dispose();
            throw;
        }
    }

    // Builds an engine that uses a detector owned by the shared cache, so it must not dispose it.
    private PaddleOCREngine(string detectionModelFolder, string recognitionModelFolder, TextDetector detector)
    {
        using SessionOptions sessionOptions = CreateSessionOptions();

        DetectionModelFolder = detectionModelFolder;
        RecognitionModelFolder = recognitionModelFolder;
        _ownsDetector = false;
        _detector = detector;
        _recognizer = new TextRecognizer(RecognitionModelFolder, sessionOptions);
    }

    private static SessionOptions CreateSessionOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
    };

    private static string[] GetRecognitionModelNames(OCRRecognitionScript script) =>
        script == OCRRecognitionScript.Cyrillic ? CyrillicRecognitionModelNames : DefaultRecognitionModelNames;

    private static string GetBuiltInRecognitionModelName(OCRRecognitionScript script) =>
        script == OCRRecognitionScript.Cyrillic ? BuiltInCyrillicRecognitionModelName : BuiltInDefaultRecognitionModelName;

    // Returns the first model in the list that has both its files. When none does, the built-in one is returned, so
    // loading it reports that model as missing.
    private static string FindModelFolder(string modelsFolder, string[] modelNames, string builtInModelName)
    {
        foreach (string modelName in modelNames)
        {
            foreach (string folderName in (string[])[modelName, modelName + DownloadFolderSuffix])
            {
                string folder = Path.Combine(modelsFolder, folderName);

                if (File.Exists(Path.Combine(folder, "inference.onnx")) && File.Exists(Path.Combine(folder, "inference.yml")))
                {
                    return folder;
                }
            }
        }

        return Path.Combine(modelsFolder, builtInModelName);
    }

    public OCRResult Recognize(SKBitmap bitmap, float minScore = DefaultMinScore)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        if (bitmap.Width <= 0 || bitmap.Height <= 0)
        {
            return new OCRResult([]);
        }

        using SKBitmap source = ImageTensor.ToOpaqueBgra(bitmap);
        using SKImage image = SKImage.FromBitmap(source);
        List<SKPoint[]> boxes = _detector.Detect(image);

        if (boxes.Count == 0)
        {
            return new OCRResult([]);
        }

        (string Text, float Score)[] texts = _recognizer.Recognize(image, boxes);
        List<OCRTextRegion> regions = [];

        for (int i = 0; i < boxes.Count; i++)
        {
            string text = texts[i].Text.Trim();

            if (text.Length > 0 && texts[i].Score >= minScore)
            {
                regions.Add(new OCRTextRegion(boxes[i], text, texts[i].Score));
            }
        }

        return new OCRResult(regions);
    }

    public void Dispose()
    {
        _recognizer.Dispose();

        if (_ownsDetector)
        {
            _detector.Dispose();
        }
    }
}
