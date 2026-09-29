import QtQuick

// CPU / GPU / memory card: big utilisation, a load bar, detail rows and a 60-second chart.
Card {
    id: card
    property var usage          // percent, or undefined when unavailable
    property var history: []
    property var metrics: []    // [{label, value, unit, color?}]
    property color chartColor: Theme.text
    readonly property bool known: usage !== undefined && usage !== null && isFinite(usage)

    Row {
        id: headline
        spacing: 6
        Text {
            id: percent
            text: card.known ? Math.round(card.usage) : "—"
            color: Theme.text
            font.family: Theme.numberFont
            font.pixelSize: 64
            font.features: { "tnum": 1 }
        }
        Text {
            anchors.baseline: percent.baseline
            text: "%"
            color: Theme.muted
            font.family: Theme.numberFont
            font.pixelSize: 26
        }
    }

    Rectangle {
        id: gauge
        anchors.top: headline.bottom
        anchors.topMargin: 2
        width: parent.width
        height: 6
        radius: 3
        color: Theme.faint
        Rectangle {
            width: parent.width * (card.known ? Math.max(0, Math.min(1, card.usage / 100)) : 0)
            height: parent.height
            radius: 3
            color: Theme.loadColor(card.known ? card.usage : 0)
            Behavior on width { NumberAnimation { duration: 350 } }
        }
    }

    Column {
        id: details
        anchors.top: gauge.bottom
        anchors.topMargin: 8
        width: parent.width
        Repeater {
            model: card.metrics
            MetricRow {
                required property var modelData
                width: parent.width
                label: modelData.label
                value: modelData.value
                unit: modelData.unit
                valueColor: modelData.color || Theme.text
            }
        }
    }

    HistoryChart {
        anchors.top: details.bottom
        anchors.topMargin: 14
        anchors.bottom: parent.bottom
        width: parent.width
        primary: card.history
        fixedMax: 100
        primaryColor: card.chartColor
    }
}
