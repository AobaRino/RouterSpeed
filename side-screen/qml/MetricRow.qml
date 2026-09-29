import QtQuick

// "温度 ........ 62 °C": label on the left, value and unit right-aligned.
Item {
    id: metric
    property string label
    property string value
    property string unit
    property color valueColor: Theme.text

    implicitHeight: 30

    Text {
        anchors.verticalCenter: parent.verticalCenter
        text: metric.label
        color: Theme.muted
        font.family: Theme.uiFont
        font.pixelSize: 17
    }
    Row {
        anchors.right: parent.right
        anchors.verticalCenter: parent.verticalCenter
        spacing: 5
        Text {
            id: valueText
            text: metric.value
            color: metric.valueColor
            font.family: Theme.numberFont
            font.pixelSize: 22
            font.features: { "tnum": 1 }
        }
        Text {
            anchors.baseline: valueText.baseline
            text: metric.unit
            color: Theme.muted
            font.family: Theme.numberFont
            font.pixelSize: 15
        }
    }
}
