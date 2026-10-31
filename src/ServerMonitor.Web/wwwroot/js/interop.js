// Chart + download helpers used by Blazor components (ApexCharts loaded from a pinned CDN version).
(function () {
    const charts = new Map();

    function bytes(v) {
        if (v == null) return "—";
        const u = ["B", "KB", "MB", "GB", "TB"]; let i = 0; let x = Math.abs(v);
        while (x >= 1024 && i < u.length - 1) { x /= 1024; i++; }
        return (Math.sign(v) * x).toFixed(i ? 2 : 0) + " " + u[i];
    }
    function rate(v) {
        if (v == null) return "—";
        let b = v * 8; const u = ["bps", "Kbps", "Mbps", "Gbps"]; let i = 0;
        while (b >= 1000 && i < u.length - 1) { b /= 1000; i++; }
        return b.toFixed(2) + " " + u[i];
    }
    function formatter(unit) {
        switch (unit) {
            case "percent": return v => v == null ? "—" : v.toFixed(1) + "%";
            case "bytes": return bytes;
            case "rate": return rate;
            case "ms": return v => v == null ? "—" : v.toFixed(v < 10 ? 2 : 1) + " ms";
            default: return v => v == null ? "—" : (Math.round(v * 100) / 100).toString();
        }
    }

    const palette = ["#e4e4e7", "#a78bfa", "#38bdf8", "#34d399", "#fbbf24", "#f472b6", "#fb7185", "#22d3ee", "#c084fc"];

    window.smCharts = {
        create(id, opts, series) {
            const el = document.getElementById(id);
            if (!el || typeof ApexCharts === "undefined") return;
            const fmt = formatter(opts.unit);
            const chart = new ApexCharts(el, {
                chart: {
                    type: opts.type || "area", height: opts.height || 240, background: "transparent",
                    foreColor: "#a1a1aa", fontFamily: "inherit",
                    animations: { enabled: true, easing: "easeinout", speed: 300, dynamicAnimation: { speed: 300 } },
                    toolbar: { show: true, tools: { download: true, selection: true, zoom: true, zoomin: true, zoomout: true, pan: true, reset: true },
                               export: { csv: { headerCategory: "time", dateFormatter: t => new Date(t).toISOString() } } },
                    zoom: { enabled: true, type: "x", autoScaleYaxis: true }
                },
                theme: { mode: "dark" },
                colors: palette,
                series: series,
                stroke: { curve: "smooth", width: 2 },
                fill: opts.type === "line" ? { type: "solid", opacity: 1 } : { type: "gradient", gradient: { shadeIntensity: 1, opacityFrom: 0.35, opacityTo: 0.02, stops: [0, 95] } },
                dataLabels: { enabled: false },
                grid: { borderColor: "rgba(161,161,170,0.12)", strokeDashArray: 3 },
                xaxis: { type: "datetime", labels: { datetimeUTC: false }, axisBorder: { show: false }, axisTicks: { show: false } },
                yaxis: { min: 0, max: opts.max ?? undefined, labels: { formatter: fmt }, forceNiceScale: true },
                tooltip: { theme: "dark", shared: true, x: { format: "yyyy-MM-dd HH:mm:ss" }, y: { formatter: fmt } },
                legend: { show: series.length > 1, position: "top", horizontalAlign: "left" },
                markers: { size: 0, hover: { size: 4 } },
                noData: { text: "No data" }
            });
            chart.render();
            charts.set(id, chart);
        },
        update(id, series) {
            const c = charts.get(id);
            if (c) c.updateSeries(series, true);
        },
        destroy(id) {
            const c = charts.get(id);
            if (c) { c.destroy(); charts.delete(id); }
        }
    };

    window.smDownload = function (fileName, contentType, text) {
        const blob = new Blob([text], { type: contentType });
        const url = URL.createObjectURL(blob);
        const a = document.createElement("a");
        a.href = url; a.download = fileName; document.body.appendChild(a); a.click(); a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
    };

    window.smPrint = function () { window.print(); };

    window.smScrollBottom = function (id) {
        const el = document.getElementById(id);
        if (el) el.scrollTop = el.scrollHeight;
    };
})();
