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
