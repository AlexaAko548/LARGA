// Draws a shift's GPS route (Driver & Shifts > Shift Logs > View route, SRS REQ-4.6-5) on a
// MapTiler map with Leaflet. The points and the distance come from ShiftRouteService; this only
// draws them.
//
// Leaflet loads lazily, the first time a route is opened - same approach as ocr.js and Tesseract.

const LEAFLET_JS = "https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.js";
const LEAFLET_CSS = "https://cdn.jsdelivr.net/npm/leaflet@1.9.4/dist/leaflet.css";

let leafletLoading = null;
const maps = new Map();

function loadLeaflet() {
    if (window.L) {
        return Promise.resolve(window.L);
    }

    leafletLoading ??= new Promise((resolve, reject) => {
        const css = document.createElement("link");
        css.rel = "stylesheet";
        css.href = LEAFLET_CSS;
        document.head.appendChild(css);

        const script = document.createElement("script");
        script.src = LEAFLET_JS;
        script.onload = () => resolve(window.L);
        script.onerror = () => {
            leafletLoading = null;
            reject(new Error("Could not load the map library - check the internet connection."));
        };
        document.head.appendChild(script);
    });

    return leafletLoading;
}

function endpoint(L, latLng, fill, label) {
    return L.circleMarker(latLng, { radius: 8, color: "#ffffff", weight: 2, fillColor: fill, fillOpacity: 1 })
        .bindTooltip(label, { direction: "top", offset: [0, -6] });
}

// points: [[lat, lng], ...] in time order (at least one). Replaces any map already in the element.
export async function showRoute(elementId, apiKey, points, startLabel, endLabel) {
    const L = await loadLeaflet();
    hideRoute(elementId);

    const element = document.getElementById(elementId);
    if (!element || !points || points.length === 0) {
        return;
    }

    const dark = document.documentElement.getAttribute("data-theme") === "dark";
    const style = dark ? "streets-v2-dark" : "streets-v2";
    const map = L.map(element);
    L.tileLayer(`https://api.maptiler.com/maps/${style}/256/{z}/{x}/{y}.png?key=${encodeURIComponent(apiKey)}`, {
        maxZoom: 19,
        attribution: '&copy; <a href="https://www.maptiler.com/copyright/" target="_blank">MapTiler</a> &copy; <a href="https://www.openstreetmap.org/copyright" target="_blank">OpenStreetMap contributors</a>',
    }).addTo(map);

    const line = L.polyline(points, { color: "#019bcf", weight: 4, opacity: 0.9 }).addTo(map);
    endpoint(L, points[0], "#16a34a", startLabel).addTo(map);
    if (points.length > 1) {
        endpoint(L, points[points.length - 1], "#dc2626", endLabel).addTo(map);
        map.fitBounds(line.getBounds(), { padding: [24, 24] });
    } else {
        map.setView(points[0], 16);
    }

    maps.set(elementId, map);
    // The modal may still be laying out when this runs.
    setTimeout(() => map.invalidateSize(), 0);
}

export function hideRoute(elementId) {
    const map = maps.get(elementId);
    if (map) {
        map.remove();
        maps.delete(elementId);
    }
}
