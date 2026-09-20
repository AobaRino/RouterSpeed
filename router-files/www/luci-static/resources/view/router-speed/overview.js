'use strict';
'require view';
'require rpc';
'require poll';
'require ui';

const callStatus = rpc.declare({ object: 'router-speed', method: 'status', expect: { '': {} } });
const callConfig = rpc.declare({ object: 'router-speed', method: 'config', expect: { '': {} } });
const callSave = rpc.declare({ object: 'router-speed', method: 'save', params: [ 'enabled', 'client', 'interface' ], expect: { '': {} } });
const callCredentials = rpc.declare({ object: 'router-speed', method: 'credentials', expect: { '': {} } });
const callRotate = rpc.declare({ object: 'router-speed', method: 'rotate_token', expect: { '': {} } });
const fields = [ 'directDown', 'directUp', 'proxyDown', 'proxyUp', 'unknownDown', 'unknownUp' ];

function rate(value) {
	if (value == null || !Number.isFinite(value) || value < 0) return '—';
	const units = [ 'B/s', 'KB/s', 'MB/s', 'GB/s' ];
	let unit = 0;
	while (value >= 1024 && unit < units.length - 1) { value /= 1024; unit++; }
	return value.toFixed(unit && value < 100 ? 1 : 0) + ' ' + units[unit];
}

function notify(message, error) {
	ui.addNotification(null, E('p', {}, message), error ? 'danger' : 'info');
}

async function copyText(text) {
	if (navigator.clipboard && window.isSecureContext) return navigator.clipboard.writeText(text);
	const area = E('textarea', { style: 'position:fixed;left:-9999px;top:0' }, text);
	document.body.appendChild(area);
	area.select();
	const ok = document.execCommand('copy');
	area.remove();
	if (!ok) throw new Error('copy_failed');
}

return view.extend({
	load: function() { return Promise.all([ callConfig(), callStatus() ]); },

	render: function(data) {
		const config = data[0];
		const values = {};
		let previous = null;
		let previousRates = null;
		let credential = null;
		let currentClient = config.client;
		const statusLabel = E('span', { class: 'rs-status' }, '连接中');
		const sampleLabel = E('span', { class: 'rs-muted' }, '等待采样');
		const scopeLabel = E('span', { class: 'rs-scope' }, '本机 ' + currentClient);
		const lossLabel = E('span', {}, '—');
		const summaryLabel = E('p', { class: 'rs-muted' });
		const endpoint = E('input', { type: 'text', readonly: true, class: 'cbi-input-text', value: location.origin + '/cgi-bin/router-speed' });
		const client = E('input', { type: 'text', class: 'cbi-input-text', value: config.client, placeholder: '192.168.233.10', 'aria-label': 'Windows 本机 IPv4' });
		const iface = E('select', { class: 'cbi-input-select', 'aria-label': 'LAN 采集接口' },
			(config.interfaces || [ config.interface ]).map(name => E('option', { value: name }, name)));
		iface.value = config.interface;
		const enabled = E('input', { type: 'checkbox', 'aria-label': '启用采集器' });
		enabled.checked = !!config.enabled;
		const token = E('input', { type: 'password', readonly: true, class: 'cbi-input-text', autocomplete: 'off', placeholder: '点击“读取密钥”后显示', 'aria-label': 'Windows 只读连接密钥' });
		const credentialStatus = E('span', { class: 'rs-muted' }, config.hasToken ? '密钥已生成' : '尚未生成密钥');

		function card(title, color, prefix, note) {
			values[prefix + 'Down'] = E('strong', {}, '—');
			values[prefix + 'Up'] = E('strong', {}, '—');
			return E('section', { class: 'rs-card', style: '--rs-accent:' + color }, [
				E('h3', {}, title),
				E('div', { class: 'rs-rate' }, [ E('span', {}, '↓ 下载'), values[prefix + 'Down'] ]),
				E('div', { class: 'rs-rate' }, [ E('span', {}, '↑ 上传'), values[prefix + 'Up'] ]),
				E('p', { class: 'rs-muted rs-note' }, note)
			]);
		}

		function offline(text, info) {
			previous = null;
			previousRates = null;
			statusLabel.textContent = text;
			statusLabel.dataset.state = 'warning';
			fields.forEach(name => { values[name].textContent = '—'; });
			summaryLabel.textContent = info;
		}

		function updateStatus(result) {
			if (!result.enabled) return offline('采集已停用', '启用采集后开始显示网速。');
			if (!result.available || !result.counters) {
				sampleLabel.textContent = '未收到有效采样';
				return offline('等待采集器', '采集尚未启动或数据已过期。不会保留旧速率，请检查接口和本机 IP。');
			}
			const next = result.counters;
			scopeLabel.textContent = '本机 ' + next.client + ' · ' + next.interface;
			lossLabel.textContent = String(next.droppedPackets || 0);
			sampleLabel.textContent = '更新于 ' + new Date(next.timestamp).toLocaleTimeString('zh-CN', { hour12: false }) + ' · 每秒刷新';
			if (!next.mapId) return offline('分流映射不可用', '等待 dae 分流映射恢复；不会改动现有代理配置。');
			let speeds = null;
			const same = previous && next.timestamp === previous.timestamp && next.instanceId === previous.instanceId;
			if (same) speeds = previousRates;
			else if (previous && next.instanceId === previous.instanceId && next.client === previous.client && next.interface === previous.interface && next.mapId === previous.mapId && next.timestamp > previous.timestamp && next.timestamp - previous.timestamp < 10000 && fields.every(name => next[name] >= previous[name])) {
				const seconds = (next.timestamp - previous.timestamp) / 1000;
				speeds = {};
				fields.forEach(name => { speeds[name] = (next[name] - previous[name]) / seconds; });
			}
			const newDrops = previous && (next.droppedPackets || 0) > (previous.droppedPackets || 0);
			previous = next;
			previousRates = speeds;
			if (!speeds) {
				fields.forEach(name => { values[name].textContent = '—'; });
				statusLabel.textContent = '建立统计基线';
				statusLabel.dataset.state = 'warning';
				summaryLabel.textContent = '正在等待第二个采样点。';
				return;
			}
			fields.forEach(name => { values[name].textContent = rate(speeds[name]); });
			const partial = speeds.unknownDown + speeds.unknownUp > 0 || newDrops;
			statusLabel.textContent = partial ? '采集中 · 部分流量未分类' : '正在采集';
			statusLabel.dataset.state = partial ? 'warning' : 'ok';
			summaryLabel.textContent = partial ? '未分类流量单独显示，不计入直连或代理。可因日志缺失、连接刚建立或报文无法解析产生。' : '当前显示所选电脑的 IPv4 公网 TCP/UDP 流量，字节数包含 IP 头。';
		}

		async function fetchCredential() {
			const result = await callCredentials();
			if (!result.token || !result.client) throw new Error('credential_unavailable');
			credential = { RouterUrl: location.origin + result.apiPath, Client: result.client, Token: result.token };
			token.value = result.token;
			credentialStatus.textContent = '仅能读取 ' + result.client + ' 的统计';
			return credential;
		}

		function button(text, action, primary) {
			const element = E('button', { class: 'cbi-button ' + (primary ? 'cbi-button-apply' : 'cbi-button-action'), type: 'button' }, text);
			element.addEventListener('click', async function() {
				element.disabled = true;
				try { await action(element); }
				catch (error) { notify('操作未完成，请检查输入或重新登录管理页面。', true); }
				finally { element.disabled = false; }
			});
			return element;
		}

		const save = button('保存采集设置', async function() {
			const address = client.value.trim();
			if (!/^(\d{1,3}\.){3}\d{1,3}$/.test(address) || address.split('.').some(value => +value > 255)) {
				notify('请输入有效的本机 IPv4 地址。', true); return;
			}
			const result = await callSave(enabled.checked, address, iface.value);
			if (!result.ok) { notify('保存失败。请确认本机 IP 和采集接口有效。', true); return; }
			currentClient = address;
			credential = null; token.value = ''; token.type = 'password';
			previous = null; previousRates = null;
			credentialStatus.textContent = '设置已更新，请重新导出 Windows 连接配置';
			await callStatus().then(updateStatus);
			notify('已保存，只更新网速采集器。');
		}, true);

		const show = button('读取密钥', async function(element) {
			if (!credential) await fetchCredential();
			token.type = token.type === 'password' ? 'text' : 'password';
			element.textContent = token.type === 'password' ? '显示密钥' : '隐藏密钥';
		});
		const copy = button('复制 Windows 连接配置', async function() {
			const config = await fetchCredential();
			await copyText(JSON.stringify(config, null, 2));
			notify('连接配置已复制。在 Windows 工具的“连接设置”中粘贴并保存。');
		});
		const download = button('下载连接配置', async function() {
			const config = await fetchCredential();
			const url = URL.createObjectURL(new Blob([ JSON.stringify(config, null, 2) ], { type: 'application/json' }));
			const link = E('a', { href: url, download: 'RouterSpeed-connection.json' });
			document.body.appendChild(link); link.click(); link.remove();
			setTimeout(function() { URL.revokeObjectURL(url); }, 1000);
		});
		const rotate = button('重置连接密钥', async function() {
			ui.showModal('重置连接密钥', [
				E('p', {}, '旧密钥会立即失效。重置后，需要把新的连接配置重新导入 Windows 工具。'),
				E('div', { class: 'right' }, [
					E('button', { class: 'cbi-button', click: ui.hideModal }, '取消'), ' ',
					button('确认重置', async function() {
						const result = await callRotate();
						if (!result.token) throw new Error('rotate_failed');
						credential = null; token.value = ''; token.type = 'password';
						show.textContent = '读取密钥';
						credentialStatus.textContent = '已生成新密钥，旧密钥已失效';
						ui.hideModal();
						notify('密钥已重置。请重新复制或下载 Windows 连接配置。');
					}, true)
				])
			]);
		});

		const root = E('div', { class: 'rs-root' }, [
			E('style', {}, '.rs-root{--rs-muted:#708096}.rs-head{display:flex;justify-content:space-between;align-items:center;gap:16px;flex-wrap:wrap;margin:10px 0 22px}.rs-head h2{margin:0}.rs-sub{display:flex;gap:14px;align-items:center;flex-wrap:wrap;margin-top:12px}.rs-status{font-weight:600}.rs-status[data-state=ok]{color:#15966b}.rs-status[data-state=warning]{color:#bd801b}.rs-muted{color:var(--rs-muted);font-size:13px;line-height:1.65}.rs-scope{padding:5px 10px;border-radius:6px;background:rgba(100,120,170,.09);font-size:13px}.rs-grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px;margin-bottom:16px}.rs-card{border:1px solid rgba(128,140,165,.25);border-top:3px solid var(--rs-accent);border-radius:8px;padding:16px 20px}.rs-card h3{margin:0 0 18px;border:0;padding:0;color:var(--rs-accent);font-size:15px}.rs-rate{display:flex;justify-content:space-between;align-items:center;gap:10px;margin:13px 0}.rs-rate span{font-size:13px;color:var(--rs-muted)}.rs-rate strong{font-size:25px;line-height:1.2;font-variant-numeric:tabular-nums;white-space:nowrap}.rs-note{margin:16px 0 0;min-height:22px}.rs-panel{margin-top:24px;padding:20px;border:1px solid rgba(128,140,165,.25);border-radius:8px}.rs-panel h3{margin:0 0 16px;padding:0 0 12px}.rs-field{display:grid;grid-template-columns:150px minmax(150px,1fr);align-items:center;gap:12px;margin:14px 0;max-width:780px}.rs-field>label{font-size:14px}.rs-field input[type=text],.rs-field input[type=password],.rs-field select{width:100%;box-sizing:border-box}.rs-actions{display:flex;gap:10px;flex-wrap:wrap;margin-top:18px}.rs-footer{display:flex;justify-content:space-between;gap:12px;flex-wrap:wrap}.rs-help{margin:10px 0 0}.rs-toggle{display:flex;align-items:center;gap:8px}@media(max-width:1000px){.rs-grid{grid-template-columns:1fr}.rs-rate strong{font-size:23px}.rs-field{grid-template-columns:1fr;gap:5px}}'),
			E('div', { class: 'rs-head' }, [
				E('div', {}, [ E('h2', {}, '网速采集器'), E('div', { class: 'rs-sub' }, [ statusLabel, scopeLabel ]) ]),
				sampleLabel
			]),
			E('div', { class: 'rs-grid' }, [ card('直连', '#139b88', 'direct', '已确认走直连出口'), card('代理', '#6574dc', 'proxy', '依据 dae 最终分流结果'), card('未分类', '#b78427', 'unknown', '暂时无法确认出口的流量') ]),
			E('div', { class: 'rs-footer rs-muted' }, [ E('span', {}, [ '采集期间累计丢包：', lossLabel ]), E('span', {}, '仅 IPv4 · 不含局域网访问') ]),
			summaryLabel,
			E('section', { class: 'rs-panel' }, [
				E('h3', {}, '采集设置'),
				E('div', { class: 'rs-field' }, [ E('label', {}, '启用采集'), E('label', { class: 'rs-toggle' }, [ enabled, '运行只读采集器' ]) ]),
				E('div', { class: 'rs-field' }, [ E('label', {}, 'Windows 本机 IPv4'), client ]),
				E('div', { class: 'rs-field' }, [ E('label', {}, 'LAN 采集接口'), iface ]),
				E('p', { class: 'rs-muted rs-help' }, '保存只会启停或重启本采集器，不重启网络和 daed。建议为 Windows 保留固定的 DHCP 地址。'),
				E('div', { class: 'rs-actions' }, [ save ])
			]),
			E('section', { class: 'rs-panel' }, [
				E('h3', {}, 'Windows 工具连接'),
				E('p', { class: 'rs-muted' }, '在 Windows 网速条上右键 → 连接设置，粘贴或导入下方配置。密钥只授权读取本机统计，不是路由器管理密码。'),
				E('div', { class: 'rs-field' }, [ E('label', {}, '数据接口'), endpoint ]),
				E('div', { class: 'rs-field' }, [ E('label', {}, '只读连接密钥'), token ]),
				credentialStatus,
				E('div', { class: 'rs-actions' }, [ show, copy, download, rotate ]),
				E('p', { class: 'rs-muted rs-help' }, '连接密钥限定配置中的本机 IPv4 使用。导出文件含此密钥，导入后可删除。当前复用路由器的局域网 HTTP 管理端口。')
			])
		]);
		updateStatus(data[1]);
		poll.add(function() { return callStatus().then(updateStatus).catch(function() { offline('连接中断', '读取失败，请检查管理会话或网络。'); }); }, 1);
		return root;
	},
	handleSaveApply: null,
	handleSave: null,
	handleReset: null
});
