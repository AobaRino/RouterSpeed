import QtQuick

// One traffic class: big download figure, upload below it, and 60 seconds of history
// (download as the filled area, upload as the white line) on a linear scale.
Item {
    id: row
    property string label
    property color accent: Theme.direct
    property var stats: ({})
    property bool connected: true
    readonly property color valueColor: connected ? Theme.text : Theme.muted

    Text {
        id: caption
        text: row.label
        color: row.accent
        font.family: Theme.uiFont
        font.pixelSize: 19
        font.bold: true
    }

    Row {
        id: downRow
        anchors.top: caption.bottom
        anchors.topMargin: 2
        spacing: 10
        Arrow { down: true; color: row.accent; width: 18; height: 12; anchors.verticalCenter: downValue.verticalCenter; anchors.verticalCenterOffset: 4 }
        Text {
            id: downValue
            text: row.stats.downText || "—"
            color: row.valueColor
            font.family: Theme.numberFont
            font.pixelSize: 64
            font.features: { "tnum": 1 }
        }
        Text {
            anchors.baseline: downValue.baseline
            text: row.stats.downUnit || ""
            color: Theme.muted
            font.family: Theme.numberFont
            font.pixelSize: 22
        }
    }

    Row {
        anchors.top: downRow.bottom
        anchors.topMargin: -6
        spacing: 10
        Arrow { down: false; color: row.accent; width: 14; height: 9; anchors.verticalCenter: upValue.verticalCenter; anchors.verticalCenterOffset: 2 }
        Text {
            id: upValue
            text: row.stats.upText || "—"
            color: row.valueColor
            font.family: Theme.numberFont
            font.pixelSize: 32
            font.features: { "tnum": 1 }
        }
        Text {
            anchors.baseline: upValue.baseline
            text: row.stats.upUnit || ""
            color: Theme.muted
            font.family: Theme.numberFont
            font.pixelSize: 17
        }
    }

    HistoryChart {
        id: chart
        anchors.right: parent.right
        anchors.top: parent.top
        anchors.topMargin: 8
        anchors.bottom: parent.bottom
        width: 390
        primary: row.stats.downHistory || []
        secondary: row.stats.upHistory || []
        primaryColor: row.accent
        secondaryColor: Theme.text
    }
    Text {
        anchors.right: chart.right
        anchors.bottom: chart.top
        anchors.bottomMargin: -2
        text: "60 秒峰值 " + (row.stats.peak || "—")
        color: Theme.muted
        font.family: Theme.uiFont
        font.pixelSize: 13
    }
}
