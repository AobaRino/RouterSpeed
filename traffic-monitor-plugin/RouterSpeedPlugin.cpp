// RouterSpeed plugin for TrafficMonitor.
//
// Polls the router's read-only API (/cgi-bin/router-speed, served by router-control) once a
// second on a worker thread and exposes the direct/proxy upload and download rates as
// TrafficMonitor display items. TrafficMonitor draws them in its main window, taskbar window
// and skins; this DLL only supplies text (or, for the two-line block, a small custom drawing).
//
// Nothing here touches the network stack, the router configuration or Explorer. The token
// is stored DPAPI-protected in TrafficMonitor's plugin config directory.

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <winhttp.h>
#include <wincrypt.h>
#include <algorithm>
#include <atomic>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include "include/PluginInterface.h"

#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "crypt32.lib")
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

namespace
{
    HINSTANCE g_module = nullptr;
    const wchar_t* const kEntropy = L"RouterSpeed readonly API token v1";
    const wchar_t* const kIniSection = L"RouterSpeed";

    // ---------------------------------------------------------------- helpers

    std::wstring Widen(const std::string& text)
    {
        if (text.empty()) return L"";
        int length = MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0);
        std::wstring result(length, L'\0');
        MultiByteToWideChar(CP_UTF8, 0, text.data(), (int)text.size(), result.data(), length);
        return result;
    }

    std::string Narrow(const std::wstring& text)
    {
        if (text.empty()) return "";
        int length = WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), nullptr, 0, nullptr, nullptr);
        std::string result(length, '\0');
        WideCharToMultiByte(CP_UTF8, 0, text.data(), (int)text.size(), result.data(), length, nullptr, nullptr);
        return result;
    }

    std::wstring Trim(std::wstring text)
    {
        const wchar_t* space = L" \t\r\n";
        size_t begin = text.find_first_not_of(space);
        if (begin == std::wstring::npos) return L"";
        size_t end = text.find_last_not_of(space);
        return text.substr(begin, end - begin + 1);
    }

    // Minimal flat-JSON readers: enough for the counters object and the LuCI export.
    bool JsonValueStart(const std::string& json, const char* key, size_t& position)
    {
        std::string quoted = std::string("\"") + key + "\"";
        size_t at = json.find(quoted);
        if (at == std::string::npos) return false;
        at += quoted.size();
        while (at < json.size() && (json[at] == ' ' || json[at] == '\t' || json[at] == '\r' || json[at] == '\n')) at++;
        if (at >= json.size() || json[at] != ':') return false;
        at++;
        while (at < json.size() && (json[at] == ' ' || json[at] == '\t' || json[at] == '\r' || json[at] == '\n')) at++;
        position = at;
        return at < json.size();
    }

    bool JsonNumber(const std::string& json, const char* key, double& value)
    {
        size_t at;
        if (!JsonValueStart(json, key, at)) return false;
        char* end = nullptr;
        value = strtod(json.c_str() + at, &end);
        return end != json.c_str() + at && std::isfinite(value);
    }

    bool JsonString(const std::string& json, const char* key, std::string& value)
    {
        size_t at;
        if (!JsonValueStart(json, key, at) || json[at] != '"') return false;
        std::string result;
        for (size_t i = at + 1; i < json.size(); i++)
        {
            char c = json[i];
            if (c == '"') { value = result; return true; }
            if (c == '\\' && i + 1 < json.size()) { c = json[++i]; if (c == 'n') c = '\n'; }
            result.push_back(c);
        }
        return false;
    }

    // DPAPI protection keyed to the current Windows user, base64 for the ini file.
    std::wstring Protect(const std::wstring& secret)
    {
        std::string bytes = Narrow(secret);
        DATA_BLOB input{ (DWORD)bytes.size(), (BYTE*)bytes.data() };
        DATA_BLOB entropy{ (DWORD)(wcslen(kEntropy) * sizeof(wchar_t)), (BYTE*)kEntropy };
        DATA_BLOB output{};
        if (!CryptProtectData(&input, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)) return L"";
        DWORD length = 0;
        CryptBinaryToStringW(output.pbData, output.cbData, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, nullptr, &length);
        std::wstring encoded(length, L'\0');
        CryptBinaryToStringW(output.pbData, output.cbData, CRYPT_STRING_BASE64 | CRYPT_STRING_NOCRLF, encoded.data(), &length);
        LocalFree(output.pbData);
        while (!encoded.empty() && encoded.back() == L'\0') encoded.pop_back();
        return encoded;
    }

    std::wstring Unprotect(const std::wstring& encoded)
    {
        if (encoded.empty()) return L"";
        DWORD length = 0;
        if (!CryptStringToBinaryW(encoded.c_str(), 0, CRYPT_STRING_BASE64, nullptr, &length, nullptr, nullptr)) return L"";
        std::vector<BYTE> bytes(length);
        CryptStringToBinaryW(encoded.c_str(), 0, CRYPT_STRING_BASE64, bytes.data(), &length, nullptr, nullptr);
        DATA_BLOB input{ length, bytes.data() };
        DATA_BLOB entropy{ (DWORD)(wcslen(kEntropy) * sizeof(wchar_t)), (BYTE*)kEntropy };
        DATA_BLOB output{};
        if (!CryptUnprotectData(&input, nullptr, &entropy, nullptr, nullptr, CRYPTPROTECT_UI_FORBIDDEN, &output)) return L"";
        std::wstring secret = Widen(std::string((char*)output.pbData, output.cbData));
        SecureZeroMemory(output.pbData, output.cbData);
        LocalFree(output.pbData);
        return secret;
    }

    bool ValidToken(const std::wstring& token)
    {
        if (token.size() < 16 || token.size() > 512) return false;
        for (wchar_t c : token)
            if (!iswalnum(c) && wcschr(L"-._~+/=", c) == nullptr) return false;
        return true;
    }

    // ---------------------------------------------------------------- HTTP

    struct HttpResult
    {
        bool transported = false;   // a response arrived at all
        DWORD status = 0;
        std::string body;
    };

    HttpResult HttpGet(const std::wstring& url, const std::wstring& token)
    {
        HttpResult result;
        URL_COMPONENTS parts{};
        parts.dwStructSize = sizeof(parts);
        wchar_t host[256]{}, path[1024]{};
        parts.lpszHostName = host; parts.dwHostNameLength = 256;
        parts.lpszUrlPath = path; parts.dwUrlPathLength = 1024;
        if (!WinHttpCrackUrl(url.c_str(), 0, 0, &parts)) return result;
        bool secure = parts.nScheme == INTERNET_SCHEME_HTTPS;
        if (!secure && parts.nScheme != INTERNET_SCHEME_HTTP) return result;

        // No system proxy, no redirects: the API lives on the LAN router and must be reached directly.
        HINTERNET session = WinHttpOpen(L"RouterSpeed-TrafficMonitor/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY, WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        if (!session) return result;
        WinHttpSetTimeouts(session, 2000, 2000, 2000, 2000);
        HINTERNET connection = WinHttpConnect(session, host, parts.nPort, 0);
        HINTERNET request = connection ? WinHttpOpenRequest(connection, L"GET", path, nullptr, WINHTTP_NO_REFERER,
            WINHTTP_DEFAULT_ACCEPT_TYPES, secure ? WINHTTP_FLAG_SECURE : 0) : nullptr;
        if (request)
        {
            DWORD disable = WINHTTP_DISABLE_REDIRECTS | WINHTTP_DISABLE_COOKIES;
            WinHttpSetOption(request, WINHTTP_OPTION_DISABLE_FEATURE, &disable, sizeof(disable));
            if (secure)
            {
                // Routers use self-signed certificates; the token is scoped to one client IP anyway.
                DWORD flags = SECURITY_FLAG_IGNORE_UNKNOWN_CA | SECURITY_FLAG_IGNORE_CERT_CN_INVALID | SECURITY_FLAG_IGNORE_CERT_DATE_INVALID;
                WinHttpSetOption(request, WINHTTP_OPTION_SECURITY_FLAGS, &flags, sizeof(flags));
            }
            std::wstring header = L"Authorization: Bearer " + token;
            if (WinHttpSendRequest(request, header.c_str(), (DWORD)header.size(), WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
                WinHttpReceiveResponse(request, nullptr))
            {
                DWORD status = 0, size = sizeof(status);
                WinHttpQueryHeaders(request, WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER, WINHTTP_HEADER_NAME_BY_INDEX, &status, &size, WINHTTP_NO_HEADER_INDEX);
                result.transported = true;
                result.status = status;
                char chunk[4096];
                DWORD read = 0;
                while (result.body.size() < 16384 && WinHttpReadData(request, chunk, sizeof(chunk), &read) && read > 0)
                    result.body.append(chunk, read);
            }
            SecureZeroMemory(header.data(), header.size() * sizeof(wchar_t));
        }
        if (request) WinHttpCloseHandle(request);
        if (connection) WinHttpCloseHandle(connection);
        WinHttpCloseHandle(session);
        return result;
    }

    // ---------------------------------------------------------------- rates

    struct Counters
    {
        double timestamp = 0, directDown = 0, directUp = 0, proxyDown = 0, proxyUp = 0, unknownDown = 0, unknownUp = 0;
        std::string instance;
    };

    struct Rates
    {
        bool connected = false;
        double directDown = 0, directUp = 0, proxyDown = 0, proxyUp = 0, unknownDown = 0, unknownUp = 0;
        std::wstring status;   // one line for the tooltip
    };

    bool ParseCounters(const std::string& json, Counters& c)
    {
        return JsonNumber(json, "timestamp", c.timestamp) && JsonNumber(json, "directDown", c.directDown) &&
            JsonNumber(json, "directUp", c.directUp) && JsonNumber(json, "proxyDown", c.proxyDown) &&
            JsonNumber(json, "proxyUp", c.proxyUp) && JsonString(json, "instanceId", c.instance) &&
            (JsonNumber(json, "unknownDown", c.unknownDown) || true) && (JsonNumber(json, "unknownUp", c.unknownUp) || true);
    }

    std::wstring FormatSpeed(double bytesPerSecond, bool connected, bool space)
    {
        if (!connected || !std::isfinite(bytesPerSecond) || bytesPerSecond < 0) return L"—";
        const wchar_t* gap = space ? L" " : L"";
        wchar_t buffer[48];
        double kb = bytesPerSecond / 1024;
        if (kb < 1000)
        {
            const wchar_t* format = kb < 10 ? L"%.2f%sKB/s" : kb < 100 ? L"%.1f%sKB/s" : L"%.0f%sKB/s";
            swprintf_s(buffer, format, kb, gap);
        }
        else
        {
            double mb = kb / 1024;
            swprintf_s(buffer, mb < 100 ? L"%.2f%sMB/s" : L"%.1f%sMB/s", mb, gap);
        }
        return buffer;
    }

    // Short form for the two-line taskbar block: 112B, 16.2K, 1.4M.
    std::wstring FormatCompact(double bytesPerSecond, bool connected)
    {
        if (!connected || !std::isfinite(bytesPerSecond) || bytesPerSecond < 0) return L"—";
        const wchar_t* units[] = { L"B", L"K", L"M", L"G", L"T" };
        int unit = 0;
        while (bytesPerSecond >= 1024 && unit < 4) { bytesPerSecond /= 1024; unit++; }
        if (bytesPerSecond >= 999.5 && unit < 4) { bytesPerSecond /= 1024; unit++; }
        wchar_t buffer[32];
        swprintf_s(buffer, unit == 0 || bytesPerSecond >= 99.95 ? L"%.0f %s" : L"%.1f %s", bytesPerSecond, units[unit]);
        return buffer;
    }

    // ---------------------------------------------------------------- settings

    struct Settings
    {
        std::wstring url;
        std::wstring token;
        std::wstring client;   // informational only; the router enforces the source address
    };

    class SettingsStore
    {
    public:
        void SetDirectory(const std::wstring& directory)
        {
            std::wstring dir = directory;
            if (!dir.empty() && dir.back() != L'\\') dir += L'\\';
            m_path = dir + L"RouterSpeed.ini";
        }

        Settings Load() const
        {
            Settings s;
            if (m_path.empty()) return s;
            wchar_t buffer[2048];
            GetPrivateProfileStringW(kIniSection, L"RouterUrl", L"", buffer, 2048, m_path.c_str()); s.url = buffer;
            GetPrivateProfileStringW(kIniSection, L"Client", L"", buffer, 2048, m_path.c_str()); s.client = buffer;
            GetPrivateProfileStringW(kIniSection, L"ProtectedToken", L"", buffer, 2048, m_path.c_str()); s.token = Unprotect(buffer);
            // A plaintext Token written by hand is accepted once, then replaced by its protected form.
            GetPrivateProfileStringW(kIniSection, L"Token", L"", buffer, 2048, m_path.c_str());
            std::wstring plain = Trim(buffer);
            if (!plain.empty())
            {
                s.token = plain;
                if (Save(s)) WritePrivateProfileStringW(kIniSection, L"Token", nullptr, m_path.c_str());
            }
            return s;
        }

        bool Save(const Settings& s) const
        {
            if (m_path.empty()) return false;
            std::wstring protectedToken = Protect(s.token);
            if (protectedToken.empty()) return false;
            return WritePrivateProfileStringW(kIniSection, L"RouterUrl", s.url.c_str(), m_path.c_str()) &&
                WritePrivateProfileStringW(kIniSection, L"Client", s.client.c_str(), m_path.c_str()) &&
                WritePrivateProfileStringW(kIniSection, L"ProtectedToken", protectedToken.c_str(), m_path.c_str());
        }

    private:
        std::wstring m_path;
    };

    // ---------------------------------------------------------------- poller

    class Poller
    {
    public:
        ~Poller() { Stop(); }

        void Start()
        {
            if (m_thread.joinable()) return;
            m_stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
            m_thread = std::thread([this] { Run(); });
        }

        void Stop()
        {
            if (!m_thread.joinable()) return;
            SetEvent(m_stop);
            m_thread.join();
            CloseHandle(m_stop);
            m_stop = nullptr;
        }

        void Configure(const Settings& settings)
        {
            std::lock_guard<std::mutex> lock(m_lock);
            m_settings = settings;
            m_previous = Counters{};
            m_rates = Rates{};
            m_rates.status = settings.url.empty() || settings.token.empty() ? L"未配置：请在插件选项中填写路由器接口地址和凭证。" : L"正在连接路由器…";
        }

        Rates Current()
        {
            std::lock_guard<std::mutex> lock(m_lock);
            return m_rates;
        }

    private:
        void Run()
        {
            while (WaitForSingleObject(m_stop, 1000) == WAIT_TIMEOUT)
            {
                Settings settings;
                { std::lock_guard<std::mutex> lock(m_lock); settings = m_settings; }
                if (settings.url.empty() || settings.token.empty()) continue;
                HttpResult response = HttpGet(settings.url, settings.token);
                Rates next;
                Counters current;
                if (!response.transported)
                    next.status = L"无法连接路由器接口。";
                else if (response.status == 401)
                    next.status = L"凭证无效或已重置，请重新导入连接信息。";
                else if (response.status == 403)
                    next.status = L"路由器拒绝了本机地址，请检查采集设置中的客户端 IPv4。";
                else if (response.status == 503)
                    next.status = L"采集器暂不可用（已停用、数据过期或 dae 映射缺失）。";
                else if (response.status != 200)
                    next.status = L"路由器返回了意外状态 " + std::to_wstring(response.status) + L"。";
                else if (!ParseCounters(response.body, current))
                    next.status = L"路由器返回的数据无法解析。";
                else
                {
                    std::lock_guard<std::mutex> lock(m_lock);
                    bool sameRun = !m_previous.instance.empty() && m_previous.instance == current.instance &&
                        current.timestamp > m_previous.timestamp && current.directDown >= m_previous.directDown &&
                        current.directUp >= m_previous.directUp && current.proxyDown >= m_previous.proxyDown &&
                        current.proxyUp >= m_previous.proxyUp;
                    if (sameRun)
                    {
                        double seconds = (current.timestamp - m_previous.timestamp) / 1000.0;
                        next.connected = true;
                        next.directDown = (current.directDown - m_previous.directDown) / seconds;
                        next.directUp = (current.directUp - m_previous.directUp) / seconds;
                        next.proxyDown = (current.proxyDown - m_previous.proxyDown) / seconds;
                        next.proxyUp = (current.proxyUp - m_previous.proxyUp) / seconds;
                        next.unknownDown = std::max(0.0, current.unknownDown - m_previous.unknownDown) / seconds;
                        next.unknownUp = std::max(0.0, current.unknownUp - m_previous.unknownUp) / seconds;
                        next.status = next.unknownDown + next.unknownUp >= 1024 ? L"已连接 · 有流量暂时未分类" : L"已连接";
                    }
                    else
                        next.status = L"已连接 · 正在建立速率基线";
                    m_previous = current;
                    m_rates = next;
                    continue;
                }
                std::lock_guard<std::mutex> lock(m_lock);
                m_previous = Counters{};
                m_rates = next;
            }
        }

        std::mutex m_lock;
        Settings m_settings;
        Counters m_previous;
        Rates m_rates;
        std::thread m_thread;
        HANDLE m_stop = nullptr;
    };

    // ---------------------------------------------------------------- options dialog

    // Built in memory so the DLL needs no resource script.
    class DialogTemplate
    {
    public:
        DialogTemplate(const wchar_t* title, int width, int height)
        {
            Write<DWORD>(DS_SETFONT | DS_MODALFRAME | DS_CENTER | WS_POPUP | WS_CAPTION | WS_SYSMENU);
            Write<DWORD>(0);
            m_countAt = m_words.size();
            Write<WORD>(0);
            Write<WORD>(0); Write<WORD>(0); Write<WORD>((WORD)width); Write<WORD>((WORD)height);
            Write<WORD>(0); Write<WORD>(0);
            WriteString(title);
            Write<WORD>(9);
            WriteString(L"Microsoft YaHei UI");
        }

        void Add(const wchar_t* cls, const wchar_t* text, WORD id, int x, int y, int w, int h, DWORD style, DWORD exStyle = 0)
        {
            Align();
            Write<DWORD>(style | WS_CHILD | WS_VISIBLE);
            Write<DWORD>(exStyle);
            Write<WORD>((WORD)x); Write<WORD>((WORD)y); Write<WORD>((WORD)w); Write<WORD>((WORD)h);
            Write<WORD>(id);
            WriteString(cls);
            WriteString(text);
            Write<WORD>(0);
            m_words[m_countAt]++;
        }

        const DLGTEMPLATE* Get() { Align(); return (const DLGTEMPLATE*)m_words.data(); }

    private:
        template <typename T> void Write(T value)
        {
            const WORD* raw = (const WORD*)&value;
            for (size_t i = 0; i < sizeof(T) / sizeof(WORD); i++) m_words.push_back(raw[i]);
        }
        void WriteString(const wchar_t* text) { while (*text) m_words.push_back(*text++); m_words.push_back(0); }
        void Align() { while (m_words.size() % 2) m_words.push_back(0); }
        std::vector<WORD> m_words;
        size_t m_countAt = 0;
    };

    enum { IdUrl = 1001, IdToken, IdClient, IdImport, IdHint };

    struct DialogState
    {
        Settings settings;
        bool changed = false;
    };

    std::wstring ControlText(HWND dialog, int id)
    {
        HWND control = GetDlgItem(dialog, id);
        int length = GetWindowTextLengthW(control);
        std::wstring text(length, L'\0');
        GetWindowTextW(control, text.data(), length + 1);
        return Trim(text);
    }

    INT_PTR CALLBACK OptionsProc(HWND dialog, UINT message, WPARAM wParam, LPARAM lParam)
    {
        auto* state = (DialogState*)GetWindowLongPtrW(dialog, GWLP_USERDATA);
        switch (message)
        {
        case WM_INITDIALOG:
            state = (DialogState*)lParam;
            SetWindowLongPtrW(dialog, GWLP_USERDATA, lParam);
            SetDlgItemTextW(dialog, IdUrl, state->settings.url.c_str());
            SetDlgItemTextW(dialog, IdToken, state->settings.token.c_str());
            SetDlgItemTextW(dialog, IdClient, state->settings.client.c_str());
            return TRUE;
        case WM_COMMAND:
            if (LOWORD(wParam) == IDCANCEL) { EndDialog(dialog, IDCANCEL); return TRUE; }
            if (LOWORD(wParam) == IDOK)
            {
                Settings next;
                next.url = ControlText(dialog, IdUrl);
                next.token = ControlText(dialog, IdToken);
                next.client = ControlText(dialog, IdClient);
                std::string pasted = Narrow(ControlText(dialog, IdImport));
                if (!pasted.empty())
                {
                    std::string value;
                    if (JsonString(pasted, "RouterUrl", value)) next.url = Trim(Widen(value));
                    if (JsonString(pasted, "Token", value)) next.token = Trim(Widen(value));
                    if (JsonString(pasted, "Client", value)) next.client = Trim(Widen(value));
                }
                if (next.url.rfind(L"http://", 0) != 0 && next.url.rfind(L"https://", 0) != 0)
                {
                    MessageBoxW(dialog, L"请输入完整的 HTTP 或 HTTPS 接口地址，例如 http://192.168.1.1/cgi-bin/router-speed。", L"RouterSpeed", MB_ICONWARNING);
                    return TRUE;
                }
                if (!ValidToken(next.token))
                {
                    MessageBoxW(dialog, L"凭证无效，请从路由器 LuCI 的网速采集器页面重新复制或导出连接信息。", L"RouterSpeed", MB_ICONWARNING);
                    return TRUE;
                }
                state->settings = next;
                state->changed = true;
                EndDialog(dialog, IDOK);
                return TRUE;
            }
            break;
        }
        return FALSE;
    }

    bool ShowOptions(HWND parent, Settings& settings)
    {
        DialogTemplate dlg(L"RouterSpeed 连接设置", 300, 190);
        dlg.Add(L"STATIC", L"路由器只读接口地址：", IdHint, 10, 10, 280, 10, SS_LEFT);
        dlg.Add(L"EDIT", L"", IdUrl, 10, 22, 280, 13, ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP);
        dlg.Add(L"STATIC", L"只读凭证 Token：", IdHint, 10, 42, 280, 10, SS_LEFT);
        dlg.Add(L"EDIT", L"", IdToken, 10, 54, 280, 13, ES_AUTOHSCROLL | ES_PASSWORD | WS_BORDER | WS_TABSTOP);
        dlg.Add(L"STATIC", L"本机 IPv4（仅备注，实际以路由器采集设置为准）：", IdHint, 10, 74, 280, 10, SS_LEFT);
        dlg.Add(L"EDIT", L"", IdClient, 10, 86, 120, 13, ES_AUTOHSCROLL | WS_BORDER | WS_TABSTOP);
        dlg.Add(L"STATIC", L"或直接粘贴 LuCI“Windows 工具连接”导出的 JSON（优先使用）：", IdHint, 10, 106, 280, 10, SS_LEFT);
        dlg.Add(L"EDIT", L"", IdImport, 10, 118, 280, 40, ES_MULTILINE | ES_AUTOVSCROLL | ES_WANTRETURN | WS_BORDER | WS_TABSTOP | WS_VSCROLL);
        dlg.Add(L"BUTTON", L"确定", IDOK, 180, 166, 50, 15, BS_DEFPUSHBUTTON | WS_TABSTOP);
        dlg.Add(L"BUTTON", L"取消", IDCANCEL, 240, 166, 50, 15, BS_PUSHBUTTON | WS_TABSTOP);
        DialogState state{ settings };
        INT_PTR result = DialogBoxIndirectParamW(g_module, dlg.Get(), parent, OptionsProc, (LPARAM)&state);
        if (result == IDOK && state.changed) { settings = state.settings; return true; }
        return false;
    }

    // ---------------------------------------------------------------- items

    class TextItem : public IPluginItem
    {
    public:
        TextItem(const wchar_t* name, const wchar_t* id, const wchar_t* label) : m_name(name), m_id(id), m_label(label) {}
        const wchar_t* GetItemName() const override { return m_name; }
        const wchar_t* GetItemId() const override { return m_id; }
        const wchar_t* GetItemLableText() const override { return m_label; }
        const wchar_t* GetItemValueText() const override { return m_value.c_str(); }
        const wchar_t* GetItemValueSampleText() const override { return L"999.9 KB/s"; }
        void SetValue(std::wstring value) { m_value = std::move(value); }
    private:
        const wchar_t* m_name;
        const wchar_t* m_id;
        const wchar_t* m_label;
        std::wstring m_value = L"—";
    };

    // Compact taskbar row: "D ▼ 16.2 K ▲ 3.8 K" (direct) or "P ▼ 1.4 M ▲ 24.0 K" (proxy). Two
    // of these pair up into TrafficMonitor's usual two-line taskbar column. Drawn with plain GDI
    // through IPluginItem::DrawItem, which every TrafficMonitor since the plugin API exists
    // supports (the released 1.86 is API 7 and has neither DrawItemEx nor double-line items).
    class RowItem : public IPluginItem
    {
    public:
        RowItem(const wchar_t* name, const wchar_t* id, const wchar_t* tag) : m_name(name), m_id(id), m_tag(tag) {}
        const wchar_t* GetItemName() const override { return m_name; }
        const wchar_t* GetItemId() const override { return m_id; }
        const wchar_t* GetItemLableText() const override { return L""; }
        const wchar_t* GetItemValueText() const override { return L""; }
        const wchar_t* GetItemValueSampleText() const override { return L""; }
        bool IsCustomDraw() const override { return true; }
        int GetItemWidth() const override { return 118; }
        int GetItemWidthEx(void* hDC) const override
        {
            SIZE text{}, tag{};
            HDC dc = (HDC)hDC;
            if (!GetTextExtentPoint32W(dc, L"999.9 K", 7, &text) || !GetTextExtentPoint32W(dc, L"P", 1, &tag)) return 0;
            Metrics m = Measure(tag.cy);
            return tag.cx + m.gap + m.arrow + m.gap + text.cx + m.column + m.arrow + m.gap + text.cx;
        }
        void SetRates(double down, double up, bool connected)
        {
            m_down = FormatCompact(down, connected);
            m_up = FormatCompact(up, connected);
        }
        void SetColors(COLORREF label, COLORREF value) { m_labelColor = label; m_valueColor = value; }

        void DrawItem(void* hDC, int x, int y, int w, int h, bool) override
        {
            HDC dc = (HDC)hDC;
            int savedMode = SetBkMode(dc, TRANSPARENT);
            COLORREF savedText = GetTextColor(dc);
            SIZE tag{}, down{}, up{};
            GetTextExtentPoint32W(dc, m_tag, 1, &tag);
            GetTextExtentPoint32W(dc, m_down.c_str(), (int)m_down.size(), &down);
            GetTextExtentPoint32W(dc, m_up.c_str(), (int)m_up.size(), &up);
            Metrics m = Measure(tag.cy);
            int textY = y + (h - tag.cy) / 2;
            int middle = y + h / 2;
            int cursor = x;
            SetTextColor(dc, m_labelColor);
            TextOutW(dc, cursor, textY, m_tag, 1);
            cursor += tag.cx + m.gap;
            Triangle(dc, cursor, middle, m.arrow, true, m_labelColor);
            cursor += m.arrow + m.gap;
            SetTextColor(dc, m_valueColor);
            TextOutW(dc, cursor, textY, m_down.c_str(), (int)m_down.size());
            cursor += down.cx + m.column;
            Triangle(dc, cursor, middle, m.arrow, false, m_labelColor);
            cursor += m.arrow + m.gap;
            TextOutW(dc, cursor, textY, m_up.c_str(), (int)m_up.size());
            (void)w;
            SetTextColor(dc, savedText);
            SetBkMode(dc, savedMode);
        }

    private:
        struct Metrics { int arrow, gap, column; };
        // Everything scales from the font's line height, which TrafficMonitor already sized for the DPI.
        static Metrics Measure(int lineHeight)
        {
            int arrow = std::max(5, lineHeight * 2 / 5) | 1;   // odd width keeps the apex centred
            return { arrow, std::max(2, lineHeight / 6), std::max(6, lineHeight / 2) };
        }

        // Squat filled triangle: width = size, height = (size + 1) / 2.
        static void Triangle(HDC dc, int left, int middle, int size, bool down, COLORREF color)
        {
            int height = (size + 1) / 2;
            int top = middle - height / 2;
            POINT points[3];
            if (down) { points[0] = { left, top }; points[1] = { left + size, top }; points[2] = { left + size / 2, top + height }; }
            else { points[0] = { left, top + height }; points[1] = { left + size, top + height }; points[2] = { left + size / 2, top }; }
            HBRUSH brush = CreateSolidBrush(color);
            HPEN pen = CreatePen(PS_SOLID, 1, color);
            HGDIOBJ oldBrush = SelectObject(dc, brush), oldPen = SelectObject(dc, pen);
            Polygon(dc, points, 3);
            SelectObject(dc, oldBrush);
            SelectObject(dc, oldPen);
            DeleteObject(brush);
            DeleteObject(pen);
        }

        const wchar_t* m_name;
        const wchar_t* m_id;
        const wchar_t* m_tag;
        std::wstring m_down = L"—";
        std::wstring m_up = L"—";
        COLORREF m_labelColor = RGB(255, 255, 255);
        COLORREF m_valueColor = RGB(255, 255, 255);
    };

    // ---------------------------------------------------------------- plugin

    class RouterSpeedPlugin : public ITMPlugin
    {
    public:
        RouterSpeedPlugin()
            : m_items{ TextItem(L"直连下载", L"routerspeed_direct_down", L"直↓"), TextItem(L"直连上传", L"routerspeed_direct_up", L"直↑"),
                       TextItem(L"代理下载", L"routerspeed_proxy_down", L"代↓"), TextItem(L"代理上传", L"routerspeed_proxy_up", L"代↑") },
              m_rows{ RowItem(L"直连（紧凑行）", L"routerspeed_direct_row", L"D"), RowItem(L"代理（紧凑行）", L"routerspeed_proxy_row", L"P") }
        {
        }

        IPluginItem* GetItem(int index) override
        {
            if (index >= 0 && index < 4) return &m_items[index];
            if (index >= 4 && index < 6) return &m_rows[index - 4];
            return nullptr;
        }

        void DataRequired() override
        {
            EnsureStarted();
            Rates rates = m_poller.Current();
            bool space = m_taskbarSpace;
            m_items[0].SetValue(FormatSpeed(rates.directDown, rates.connected, space));
            m_items[1].SetValue(FormatSpeed(rates.directUp, rates.connected, space));
            m_items[2].SetValue(FormatSpeed(rates.proxyDown, rates.connected, space));
            m_items[3].SetValue(FormatSpeed(rates.proxyUp, rates.connected, space));
            m_rows[0].SetRates(rates.directDown, rates.directUp, rates.connected);
            m_rows[1].SetRates(rates.proxyDown, rates.proxyUp, rates.connected);
            m_tooltip = L"RouterSpeed：" + rates.status;
            if (rates.connected && rates.unknownDown + rates.unknownUp >= 1024)
                m_tooltip += L"\n未分类 ↓" + FormatSpeed(rates.unknownDown, true, true) + L" ↑" + FormatSpeed(rates.unknownUp, true, true);
        }

        OptionReturn ShowOptionsDialog(void* hParent) override
        {
            Settings settings = m_store.Load();
            if (!ShowOptions((HWND)hParent, settings)) return OR_OPTION_UNCHANGED;
            if (!m_store.Save(settings))
            {
                MessageBoxW((HWND)hParent, L"无法保存连接设置。", L"RouterSpeed", MB_ICONERROR);
                return OR_OPTION_UNCHANGED;
            }
            m_poller.Configure(settings);
            return OR_OPTION_CHANGED;
        }

        const wchar_t* GetInfo(PluginInfoIndex index) override
        {
            switch (index)
            {
            case TMI_NAME: return L"RouterSpeed";
            case TMI_DESCRIPTION: return L"显示路由器 dae 分流后本机的直连 / 代理实时网速。";
            case TMI_AUTHOR: return L"AobaRino";
            case TMI_COPYRIGHT: return L"MIT";
            case TMI_VERSION: return L"0.1";
            case TMI_URL: return L"https://github.com/AobaRino/RouterSpeed";
            default: return L"";
            }
        }

        const wchar_t* GetTooltipInfo() override { return m_tooltip.c_str(); }

        void OnExtenedInfo(ExtendedInfoIndex index, const wchar_t* data) override
        {
            switch (index)
            {
            case EI_CONFIG_DIR: m_store.SetDirectory(data); break;
            case EI_LABEL_TEXT_COLOR: m_labelColor = wcstoul(data, nullptr, 10); ApplyColors(); break;
            case EI_VALUE_TEXT_COLOR: m_valueColor = wcstoul(data, nullptr, 10); ApplyColors(); break;
            case EI_TASKBAR_WND_SPERATE_WITH_SPACE: m_taskbarSpace = wcscmp(data, L"0") != 0; break;
            default: break;
            }
        }

        void OnInitialize(ITrafficMonitor* app) override
        {
            if (app != nullptr) m_store.SetDirectory(app->GetPluginConfigDir());
            EnsureStarted();
        }

    private:
        void ApplyColors()
        {
            m_rows[0].SetColors(m_labelColor, m_valueColor);
            m_rows[1].SetColors(m_labelColor, m_valueColor);
        }

        void EnsureStarted()
        {
            if (m_started) return;
            m_started = true;
            m_poller.Configure(m_store.Load());
            m_poller.Start();
        }

        TextItem m_items[4];
        RowItem m_rows[2];
        SettingsStore m_store;
        Poller m_poller;
        std::wstring m_tooltip = L"RouterSpeed：正在启动";
        unsigned long m_labelColor = 0xFFFFFF;
        unsigned long m_valueColor = 0xFFFFFF;
        bool m_taskbarSpace = true;
        bool m_started = false;
    };

    RouterSpeedPlugin* g_plugin = nullptr;
}

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance()
{
    // Leaked on purpose: TrafficMonitor keeps the pointer for the life of the process, and
    // joining the poller from DllMain would deadlock on the loader lock.
    if (g_plugin == nullptr) g_plugin = new RouterSpeedPlugin();
    return g_plugin;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}
