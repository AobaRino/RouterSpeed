import QtQuick

// The squat filled triangle used by the Windows panel: ▼ download, ▲ upload.
Canvas {
    id: arrow
    property bool down: true
    property color color: Theme.text
    implicitWidth: 14
    implicitHeight: 9

    onColorChanged: requestPaint()
    onPaint: {
        const ctx = getContext("2d")
        ctx.reset()
        ctx.fillStyle = color
        ctx.beginPath()
        if (down) {
            ctx.moveTo(0, 0); ctx.lineTo(width, 0); ctx.lineTo(width / 2, height)
        } else {
            ctx.moveTo(0, height); ctx.lineTo(width, height); ctx.lineTo(width / 2, 0)
        }
        ctx.closePath()
        ctx.fill()
    }
}
