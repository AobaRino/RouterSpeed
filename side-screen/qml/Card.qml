import QtQuick

// A titled panel; children go into the padded body below the title.
Rectangle {
    id: card
    property string title
    property string subtitle
    property color accent: Theme.text
    default property alias content: body.data

    radius: 12
    color: Theme.card

    Text {
        id: heading
        x: 20
        y: 12
        text: card.title
        color: card.accent
        font.family: Theme.uiFont
        font.pixelSize: 20
        font.bold: true
    }
    Text {
        anchors.right: parent.right
        anchors.rightMargin: 20
        anchors.baseline: heading.baseline
        width: Math.max(0, card.width - heading.width - 60)
        horizontalAlignment: Text.AlignRight
        elide: Text.ElideRight
        text: card.subtitle
        color: Theme.muted
        font.family: Theme.uiFont
        font.pixelSize: 15
    }
    Item {
        id: body
        anchors.fill: parent
        anchors.topMargin: 48
        anchors.leftMargin: 20
        anchors.rightMargin: 20
        anchors.bottomMargin: 16
    }
}
