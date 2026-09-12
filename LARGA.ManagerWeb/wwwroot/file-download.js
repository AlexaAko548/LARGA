// Triggers a browser download of in-memory text content - used by the Generate Reports
// modal to hand a manager a CSV file without needing a dedicated download endpoint.
export function downloadFile(filename, content, mimeType) {
  const blob = new Blob([content], { type: mimeType });
  const url = URL.createObjectURL(blob);

  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = filename;
  document.body.appendChild(anchor);
  anchor.click();
  document.body.removeChild(anchor);

  URL.revokeObjectURL(url);
}

window.largaDownloadFile = downloadFile;
