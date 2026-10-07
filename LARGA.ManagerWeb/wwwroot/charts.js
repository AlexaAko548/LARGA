// Dashboard charts (DashboardChart.razor) drawn with Chart.js 4 - self-hosted in lib/chartjs
// (MIT licence there), loaded on first use. Colors come from the page's CSS tokens, and every
// chart redraws when the System / Light / Dark theme changes (data-theme on <html>).

const charts = new Map(); // canvas -> { chart, spec }
let chartJsLoading = null;

function loadChartJs() {
    if (window.Chart) return Promise.resolve();
    chartJsLoading ??= new Promise((resolve, reject) => {
        const script = document.createElement('script');
        script.src = '/lib/chartjs/chart.umd.min.js';
        script.onload = () => resolve();
        script.onerror = () => { chartJsLoading = null; reject(new Error('Chart.js failed to load')); };
        document.head.appendChild(script);
    });
    return chartJsLoading;
}

// Light-mode fallbacks match app.css, where these tokens are only defined for dark mode.
const TOKENS = {
    accent: ['--accent', '#019bcf'],
    success: ['--success', '#1b8b52'],
    warning: ['--warning', '#b05a14'],
    danger: ['--danger', '#b53d4a'],
    violet: ['--violet', '#6a5acd'],
    muted: ['--muted', '#5a7a8a'],
    ink: ['--ink', '#1d1d1d'],
    surface: ['--surface', '#ffffff'],
};

function token(name) {
    const [cssVar, fallback] = TOKENS[name] ?? [null, name]; // not a token: a literal color
    if (!cssVar) return fallback;
    const value = getComputedStyle(document.documentElement).getPropertyValue(cssVar).trim();
    return value || fallback;
}

function withAlpha(color, alpha) {
    if (!color.startsWith('#') || color.length !== 7) return color;
    const n = parseInt(color.slice(1), 16);
    return `rgba(${n >> 16}, ${(n >> 8) & 255}, ${n & 255}, ${alpha})`;
}

const PALETTE = ['accent', 'success', 'warning', 'violet', 'danger', 'muted'];

function formatValue(spec, value) {
    const number = Number(value).toLocaleString('en-PH', { maximumFractionDigits: spec.decimals ?? 0 });
    return `${spec.prefix ?? ''}${number}${spec.suffix ?? ''}`;
}

function buildConfig(spec) {
    const ink = token('ink');
    const muted = token('muted');
    const grid = withAlpha(token('accent'), 0.14);
    const font = { family: getComputedStyle(document.body).fontFamily, size: 11 };
    const isDoughnut = spec.kind === 'doughnut';
    const horizontal = spec.kind === 'hbar';

    const datasets = spec.datasets.map((d, i) => {
        if (isDoughnut) {
            const colors = spec.labels.map((_, j) => token(d.colors?.[j] ?? PALETTE[j % PALETTE.length]));
            return {
                label: d.label, data: d.data,
                backgroundColor: colors, borderColor: token('surface'), borderWidth: 2, hoverOffset: 6,
            };
        }
        const color = token(d.color ?? PALETTE[i % PALETTE.length]);
        // Stacked: only the top segment is rounded, so the segments meet flush.
        const isTop = i === spec.datasets.length - 1;
        return {
            label: d.label, data: d.data,
            backgroundColor: withAlpha(color, 0.85), hoverBackgroundColor: color,
            borderRadius: spec.stacked && !isTop ? 0 : 6,
            borderSkipped: spec.stacked ? 'start' : false,
            maxBarThickness: 38,
        };
    });

    const valueAxis = {
        beginAtZero: true,
        max: spec.max ?? undefined,
        stacked: !!spec.stacked,
        grid: { color: grid },
        border: { display: false },
        // A small fixed scale (e.g. 0-7 days) gets every whole step.
        ticks: { color: muted, font, maxTicksLimit: 8, stepSize: spec.max && spec.max <= 10 ? 1 : undefined, callback: v => formatValue(spec, v) },
    };
    const categoryAxis = {
        stacked: !!spec.stacked,
        grid: { display: false },
        border: { display: false },
        ticks: { color: muted, font },
    };

    return {
        type: isDoughnut ? 'doughnut' : 'bar',
        data: { labels: spec.labels, datasets },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: { duration: 500 },
            indexAxis: horizontal ? 'y' : 'x',
            cutout: isDoughnut ? '62%' : undefined,
            layout: { padding: 4 },
            scales: isDoughnut ? {} : horizontal
                ? { x: valueAxis, y: categoryAxis }
                : { x: categoryAxis, y: valueAxis },
            plugins: {
                legend: {
                    display: isDoughnut || spec.datasets.length > 1,
                    position: isDoughnut ? 'right' : 'bottom',
                    labels: { color: ink, font, boxWidth: 10, boxHeight: 10, usePointStyle: true, pointStyle: 'circle' },
                },
                tooltip: {
                    callbacks: {
                        label: ctx => {
                            const value = isDoughnut ? ctx.parsed : (horizontal ? ctx.parsed.x : ctx.parsed.y);
                            const name = isDoughnut ? ctx.label : ctx.dataset.label;
                            if (isDoughnut) {
                                const total = ctx.dataset.data.reduce((a, b) => a + b, 0);
                                const share = total > 0 ? ` (${Math.round(100 * value / total)}%)` : '';
                                return ` ${name}: ${formatValue(spec, value)}${share}`;
                            }
                            return ` ${name}: ${formatValue(spec, value)}`;
                        },
                        footer: items => spec.footers?.[items[0]?.dataIndex] ?? '',
                    },
                },
            },
        },
    };
}

export async function render(canvas, spec) {
    if (!canvas) return;
    await loadChartJs();
    destroy(canvas);
    const chart = new window.Chart(canvas, buildConfig(spec));
    charts.set(canvas, { chart, spec });
}

export function destroy(canvas) {
    const entry = charts.get(canvas);
    if (entry) {
        entry.chart.destroy();
        charts.delete(canvas);
    }
}

// Theme switch: rebuild each chart with the new token colors (and drop detached canvases).
new MutationObserver(() => {
    for (const [canvas, { chart, spec }] of [...charts]) {
        chart.destroy();
        if (canvas.isConnected) {
            charts.set(canvas, { chart: new window.Chart(canvas, buildConfig(spec)), spec });
        } else {
            charts.delete(canvas);
        }
    }
}).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
