pragma Singleton
import QtQuick

// Colours match the Windows panel and tray icon: black background, cyan for direct,
// violet for proxy. Load and temperature colours only change when a value needs attention.
QtObject {
    readonly property color background: "#000000"
    readonly property color card: "#0b0f14"
    readonly property color faint: "#1b222c"
    readonly property color text: "#f6fafc"
    readonly property color muted: "#7d8898"
    readonly property color direct: "#58d6d7"
    readonly property color proxy: "#b498ff"
    readonly property color good: "#40dc80"
    readonly property color warn: "#ffa030"
    readonly property color hot: "#ff5a5a"
    readonly property string uiFont: "Microsoft YaHei UI"
    readonly property string numberFont: "Segoe UI"

    function number(value, digits) {
        return value === undefined || value === null || !isFinite(value) ? "—" : Number(value).toFixed(digits)
    }
    function loadColor(percent) {
        return percent >= 90 ? hot : percent >= 70 ? warn : good
    }
    function temperatureColor(celsius) {
        return celsius >= 85 ? hot : celsius >= 70 ? warn : text
    }
}
