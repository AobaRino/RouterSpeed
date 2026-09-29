import QtQuick
import QtQuick.Window

// 1920×480 dashboard for an 8.8-inch bar display: a status line, then network (direct /
// proxy), CPU, GPU and memory cards. 16px outer margins and gaps: 16+760+16+360×3+16×3+16.
Window {
    id: root
    required property var dashboard
    property bool simulator: true

    readonly property var net: dashboard.network || ({})
    readonly property var cpu: dashboard.cpu || ({})
    readonly property var gpu: dashboard.gpu || ({})
    readonly property var memory: dashboard.memory || ({})
    readonly property var disk: dashboard.disk || ({})

    width: 1920
    height: 480
    minimumWidth: simulator ? 1920 : 0
    maximumWidth: simulator ? 1920 : 100000
    minimumHeight: simulator ? 480 : 0
    maximumHeight: simulator ? 480 : 100000
    visible: true
    color: Theme.background
    title: simulator ? "RouterSpeed 副屏模拟器 · 1920×480" : "RouterSpeed"

    Item {
        id: statusBar
        x: 16
        width: parent.width - 32
        height: 44

        Row {
            anchors.verticalCenter: parent.verticalCenter
            spacing: 10
            Rectangle {
                anchors.verticalCenter: parent.verticalCenter
                width: 10; height: 10; radius: 5
                color: root.dashboard.connected ? Theme.good : Theme.warn
            }
            Text {
                text: root.dashboard.status
                color: Theme.muted
                font.family: Theme.uiFont
                font.pixelSize: 16
            }
            Rectangle {
                visible: root.dashboard.simulated
                anchors.verticalCenter: parent.verticalCenter
                width: simulatedLabel.implicitWidth + 16
                height: 22
                radius: 11
                color: Theme.faint
                Text {
                    id: simulatedLabel
                    anchors.centerIn: parent
                    text: "模拟数据"
                    color: Theme.warn
                    font.family: Theme.uiFont
                    font.pixelSize: 13
                }
            }
        }

        Row {
            anchors.right: parent.right
            anchors.verticalCenter: parent.verticalCenter
            spacing: 12
            Text {
                id: dateText
                anchors.baseline: clock.baseline
                color: Theme.muted
                font.family: Theme.uiFont
                font.pixelSize: 16
            }
            Text {
                id: clock
                color: Theme.text
                font.family: Theme.numberFont
                font.pixelSize: 24
                font.features: { "tnum": 1 }
            }
            Timer {
                interval: 1000
                repeat: true
                running: true
                triggeredOnStart: true
                onTriggered: {
                    const now = new Date()
                    clock.text = Qt.formatDateTime(now, "HH:mm:ss")
                    dateText.text = Qt.locale("zh_CN").toString(now, "M月d日 dddd")
                }
            }
        }
    }

    Row {
        x: 16
        y: statusBar.height
        height: parent.height - statusBar.height - 16
        spacing: 16

        Card {
            width: 760
            height: parent.height
            title: "网络"
            subtitle: "近 60 秒 · 线性刻度 · 面积为下载，白线为上传"
            Column {
                anchors.fill: parent
                spacing: 14
                NetworkRow {
                    width: parent.width
                    height: (parent.height - parent.spacing) / 2
                    label: "直连"
                    accent: Theme.direct
                    stats: root.net.direct || ({})
                    connected: root.dashboard.connected
                }
                NetworkRow {
                    width: parent.width
                    height: (parent.height - parent.spacing) / 2
                    label: "代理"
                    accent: Theme.proxy
                    stats: root.net.proxy || ({})
                    connected: root.dashboard.connected
                }
            }
        }

        StatCard {
            width: 360
            height: parent.height
            title: "CPU"
            subtitle: root.cpu.name || ""
            usage: root.cpu.usage
            history: root.cpu.history || []
            metrics: [
                { label: "温度", value: Theme.number(root.cpu.temperature, 0), unit: "°C", color: Theme.temperatureColor(root.cpu.temperature) },
                { label: "频率", value: Theme.number(root.cpu.frequency, 2), unit: "GHz" },
                { label: "功耗", value: Theme.number(root.cpu.power, 0), unit: "W" }
            ]
        }

        StatCard {
            width: 360
            height: parent.height
            title: "GPU"
            subtitle: root.gpu.name || ""
            usage: root.gpu.usage
            history: root.gpu.history || []
            metrics: [
                { label: "温度", value: Theme.number(root.gpu.temperature, 0), unit: "°C", color: Theme.temperatureColor(root.gpu.temperature) },
                { label: "频率", value: Theme.number(root.gpu.frequency, 2), unit: "GHz" },
                { label: "显存", value: Theme.number(root.gpu.memoryUsed, 1) + " / " + Theme.number(root.gpu.memoryTotal, 0), unit: "GB" },
                { label: "功耗", value: Theme.number(root.gpu.power, 0), unit: "W" }
            ]
        }

        StatCard {
            width: 360
            height: parent.height
            title: "内存"
            usage: root.memory.usage
            history: root.memory.history || []
            metrics: [
                { label: "已用", value: Theme.number(root.memory.used, 1) + " / " + Theme.number(root.memory.total, 0), unit: "GB" },
                { label: "磁盘读", value: root.disk.readText || "—", unit: root.disk.readUnit || "" },
                { label: "磁盘写", value: root.disk.writeText || "—", unit: root.disk.writeUnit || "" }
            ]
        }
    }
}
