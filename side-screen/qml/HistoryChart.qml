import QtQuick

// The last `capacity` seconds on a linear scale, newest on the right: a filled area for the
// primary series and a line for the optional secondary one. Auto-scales to the visible peak
// unless fixedMax is set (100 for percentages).
Canvas {
    id: chart
    property var primary: []
    property var secondary: []
    property color primaryColor: Theme.direct
    property color secondaryColor: Theme.text
    property real fixedMax: 0
    property int capacity: 60

    onPrimaryChanged: requestPaint()
    onSecondaryChanged: requestPaint()
    onWidthChanged: requestPaint()
    onHeightChanged: requestPaint()

    onPaint: {
        const ctx = getContext("2d")
        ctx.reset()
        const w = width, h = height
        let max = fixedMax
        if (max <= 0) {
            max = 1
            for (const v of primary) max = Math.max(max, v)
            for (const v of secondary) max = Math.max(max, v)
            max *= 1.08
        }
        ctx.strokeStyle = Theme.faint
        ctx.lineWidth = 1
        for (let i = 1; i < 4; i++) {
            const y = Math.round(h * i / 4) + 0.5
            ctx.beginPath()
            ctx.moveTo(0, y)
            ctx.lineTo(w, y)
            ctx.stroke()
        }
        const step = w / (capacity - 1)
        function trace(values) {
            const offset = capacity - values.length
            ctx.beginPath()
            for (let i = 0; i < values.length; i++) {
                const x = (offset + i) * step
                const y = h - Math.min(values[i], max) / max * (h - 1)
                if (i === 0) ctx.moveTo(x, y)
                else ctx.lineTo(x, y)
            }
        }
        if (primary.length > 1) {
            trace(primary)
            ctx.lineTo(w, h)
            ctx.lineTo((capacity - primary.length) * step, h)
            ctx.closePath()
            ctx.fillStyle = Qt.rgba(primaryColor.r, primaryColor.g, primaryColor.b, 0.22)
            ctx.fill()
            trace(primary)
            ctx.strokeStyle = primaryColor
            ctx.lineWidth = 2
            ctx.stroke()
        }
        if (secondary.length > 1) {
            trace(secondary)
            ctx.strokeStyle = secondaryColor
            ctx.lineWidth = 1.5
            ctx.stroke()
        }
    }
}
