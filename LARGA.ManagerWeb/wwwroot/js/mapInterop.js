// mapInterop.js
// Handles MapLibre GL JS initialization and interop with Blazor

let map;

// Default center: Talisay City, Cebu, Philippines (BLM Taxi's operating area)
const DEFAULT_LAT = 10.2439;
const DEFAULT_LNG = 123.8333;
const DEFAULT_ZOOM = 13;

export function initMap(containerId, styleUrl, centerLng = DEFAULT_LNG, centerLat = DEFAULT_LAT, zoom = DEFAULT_ZOOM) {
    map = new maplibregl.Map({
        container: containerId,
        style: styleUrl,
        center: [centerLng, centerLat],
        zoom: zoom
    });

    map.addControl(new maplibregl.NavigationControl(), 'top-right');

    return true;
}

export function addMarker(lng, lat, label) {
    if (!map) return false;

    const marker = new maplibregl.Marker()
        .setLngLat([lng, lat])
        .setPopup(new maplibregl.Popup().setText(label))
        .addTo(map);

    return true;
}