// Reads the text off an uploaded image, entirely in the manager's browser (Tesseract.js) -
// the image never leaves the page for OCR and there's no cloud OCR API to enable or pay for.
// Used by the Financial Ledger (GCash receipts -> GcashReceiptParser.cs) and Driver & Shifts
// (LTO licenses -> LtoLicenseParser.cs); interpreting the text happens server-side in those.
//
// Tesseract loads lazily, only the first time something is scanned: the library plus its
// English language data are a few MB, which there's no reason to make every page visit
// download.

const TESSERACT_URL = "https://cdn.jsdelivr.net/npm/tesseract.js@5.1.1/dist/tesseract.min.js";

let tesseractLoading = null;

function loadTesseract() {
    if (window.Tesseract) {
        return Promise.resolve(window.Tesseract);
    }

    tesseractLoading ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = TESSERACT_URL;
        script.onload = () => resolve(window.Tesseract);
        script.onerror = () => {
            tesseractLoading = null;
            reject(new Error("Could not load the OCR library - check the internet connection."));
        };
        document.head.appendChild(script);
    });

    return tesseractLoading;
}

// Enlarged + grayscale copy of the image. Tesseract misreads small text (a receipt's grey
// date/reference line, a license's field values) far less often once it's scaled up; it
// still does its own black/white thresholding, which handled grey text better in testing
// than a fixed cutoff.
async function enlargedGrayscale(file) {
    const bitmap = await createImageBitmap(file);
    const scale = Math.min(3, 2400 / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement("canvas");
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);

    const ctx = canvas.getContext("2d");
    ctx.imageSmoothingQuality = "high";
    ctx.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close();

    const image = ctx.getImageData(0, 0, canvas.width, canvas.height);
    const px = image.data;
    for (let i = 0; i < px.length; i += 4) {
        const y = 0.299 * px[i] + 0.587 * px[i + 1] + 0.114 * px[i + 2];
        px[i] = px[i + 1] = px[i + 2] = y;
    }
    ctx.putImageData(image, 0, 0);
    return canvas;
}

// Returns { texts, previewUrl } for the file currently selected in `input` (an
// <input type="file">, or the element behind Blazor's <InputFile>), or null if none.
// `texts` holds two OCR passes - enlarged/grayscale first, then the image as uploaded -
// since each gets different fields right; the C# parsers pick per field.
export async function readImage(input) {
    const file = input?.files?.[0];
    if (!file) {
        return null;
    }

    const Tesseract = await loadTesseract();
    const worker = await Tesseract.createWorker("eng");
    try {
        const enhanced = await worker.recognize(await enlargedGrayscale(file));
        const original = await worker.recognize(file);
        return {
            texts: [enhanced.data.text ?? "", original.data.text ?? ""],
            previewUrl: URL.createObjectURL(file),
        };
    } finally {
        await worker.terminate();
    }
}

export function releasePreview(url) {
    if (url) {
        URL.revokeObjectURL(url);
    }
}

// -- Driver face crop (profile picture) ----------------------------------
// Same idea as the OCR above: face-api.js runs in the manager's browser, so the license photo
// isn't sent anywhere to find the face. Loaded lazily, on the first license scan only.

const FACE_API_URL = "https://cdn.jsdelivr.net/npm/face-api.js@0.22.2/dist/face-api.min.js";
const FACE_WEIGHTS_URL = "https://cdn.jsdelivr.net/gh/justadudewhohacks/face-api.js@0.22.2/weights";

// Crop width as a multiple of the face's larger side, so the avatar keeps hair and shoulders.
const FACE_CONTEXT_SCALE = 1.8;
const FACE_OUTPUT_PX = 256;

let faceApiLoading = null;

function loadFaceApi() {
    if (window.faceapi?.nets?.tinyFaceDetector?.isLoaded) {
        return Promise.resolve(window.faceapi);
    }

    faceApiLoading ??= new Promise((resolve, reject) => {
        const script = document.createElement("script");
        script.src = FACE_API_URL;
        script.onload = async () => {
            try {
                await window.faceapi.nets.tinyFaceDetector.loadFromUri(FACE_WEIGHTS_URL);
                resolve(window.faceapi);
            } catch (err) {
                faceApiLoading = null;
                reject(err);
            }
        };
        script.onerror = () => {
            faceApiLoading = null;
            reject(new Error("Could not load the face detector - check the internet connection."));
        };
        document.head.appendChild(script);
    });

    return faceApiLoading;
}

// Finds the largest face in the file selected in `input` and returns a square, face-centred
// JPEG as a data URL, or null when no face is found. EXIF rotation is applied by
// createImageBitmap, so the detected box matches the upright photo.
export async function cropDriverFace(input) {
    const file = input?.files?.[0];
    if (!file) {
        return null;
    }

    const faceapi = await loadFaceApi();
    const bitmap = await createImageBitmap(file);
    try {
        const source = document.createElement("canvas");
        source.width = bitmap.width;
        source.height = bitmap.height;
        source.getContext("2d").drawImage(bitmap, 0, 0);

        const detections = await faceapi.detectAllFaces(
            source,
            new faceapi.TinyFaceDetectorOptions({ inputSize: 608, scoreThreshold: 0.4 }));
        if (detections.length === 0) {
            return null;
        }

        // A license has one face; if the detector finds more, the largest is the holder.
        const { box } = detections.reduce((best, d) =>
            d.box.width * d.box.height > best.box.width * best.box.height ? d : best);

        const side = Math.min(
            Math.max(box.width, box.height) * FACE_CONTEXT_SCALE,
            bitmap.width,
            bitmap.height);
        const x = Math.min(Math.max(box.x + box.width / 2 - side / 2, 0), bitmap.width - side);
        const y = Math.min(Math.max(box.y + box.height / 2 - side / 2, 0), bitmap.height - side);

        const output = document.createElement("canvas");
        output.width = FACE_OUTPUT_PX;
        output.height = FACE_OUTPUT_PX;
        const ctx = output.getContext("2d");
        ctx.imageSmoothingQuality = "high";
        ctx.drawImage(source, x, y, side, side, 0, 0, FACE_OUTPUT_PX, FACE_OUTPUT_PX);
        return output.toDataURL("image/jpeg", 0.9);
    } finally {
        bitmap.close();
    }
}
