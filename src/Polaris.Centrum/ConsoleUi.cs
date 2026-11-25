namespace Polaris.Centrum;

/// <summary>The page of the console: own HTML, CSS and JavaScript, no frameworks, nothing from other servers. Every text
/// from the centre goes into the page as text (never as HTML).</summary>
public static class ConsoleUi
{
    public const string Html = """
<!doctype html>
<html lang="sk">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>POLARIS konzola</title>
<link rel="stylesheet" href="/konzola.css">
<script src="/konzola.js" defer></script>
</head>
<body>
<header class="bar">
  <div class="brand"><span class="star">✦</span> POLARIS <span class="dim">konzola</span></div>
  <div class="where"><span id="console"></span><span class="sep">·</span><span id="centre"></span></div>
  <div class="status"><span id="dot" class="dot"></span><span id="status">pripájam…</span></div>
  <button id="add" class="primary">+ Pridať PC</button>
</header>
<main>
  <section class="tools">
    <div id="chips" class="chips"></div>
    <input id="search" type="search" placeholder="Hľadať PC…" autocomplete="off">
  </section>
  <div id="message" class="message" hidden></div>
  <div id="groups"></div>
  <p id="empty" class="empty" hidden>Centrum zatiaľ nepozná žiadne PC. Klikni na <b>+ Pridať PC</b>, vydaj kód a na PC ho zadaj.</p>
</main>
<aside id="detail" class="detail" hidden>
  <div class="detail-head">
    <div><h2 id="d-name"></h2><div id="d-sub" class="dim"></div></div>
    <button id="d-close" class="ghost" aria-label="Zavrieť">✕</button>
  </div>
  <div id="d-body"></div>
</aside>
<div id="modal" class="modal" hidden>
  <div class="card">
    <div id="m-form">
      <h2>Pridať PC do skupiny</h2>
      <p class="dim">Konzola vydá kód. Na PC ho zadáš príkazom (ako správca); PC si vytvorí vlastné kľúče, dostane skupinu
      a bude dôverovať tvojim konzolám.</p>
      <label>Skupina <input id="m-group" list="m-groups" value="ucebna" maxlength="40"></label>
      <datalist id="m-groups"></datalist>
      <div class="row">
        <label>Platí dní <select id="m-days"><option>1</option><option>2</option><option>3</option><option>7</option></select></label>
        <label>Najviac PC <input id="m-count" type="number" min="1" max="200" value="30"></label>
      </div>
      <div class="actions"><button id="m-cancel" class="ghost">Zrušiť</button><button id="m-go" class="primary">Vydať kód</button></div>
    </div>
    <div id="m-result" hidden>
      <h2>Kód pre skupinu <span id="r-group"></span></h2>
      <div id="r-code" class="code"></div>
      <p id="r-until" class="dim"></p>
      <p>Na PC spusti v PowerShelli ako správca (agent už beží ako služba):</p>
      <div class="command"><code id="r-command"></code><button id="r-copy" class="ghost">Kopírovať</button></div>
      <p id="r-consoles" class="dim"></p>
      <p class="dim">Kód nikam neukladaj – centrum pozná len hodnotu z neho odvodenú.</p>
      <div class="actions"><button id="r-done" class="primary">Hotovo</button></div>
    </div>
  </div>
</div>
<div id="confirm" class="modal" hidden>
  <div class="card">
    <h2 id="c-title"></h2>
    <p id="c-text" class="dim"></p>
    <div class="actions"><button id="c-no" class="ghost">Nie</button><button id="c-yes" class="danger">Áno, odobrať</button></div>
  </div>
</div>
<div id="toast" class="toast" hidden></div>
</body>
</html>
""";

    public const string Style = """
:root {
  --bg: #f4f6f9; --panel: #ffffff; --line: #e1e6ee; --text: #1b2330; --dim: #627085;
  --accent: #2f6fdf; --accent-text: #ffffff; --ok: #1f9d55; --warn: #d98b0b; --bad: #d64545; --unknown: #8a94a6;
  --shadow: 0 1px 2px rgba(20, 30, 50, .06), 0 4px 16px rgba(20, 30, 50, .06);
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #11161f; --panel: #19202c; --line: #2a3342; --text: #e6ebf2; --dim: #95a1b5;
    --accent: #5b8ff0; --accent-text: #0b1220; --ok: #3cc47a; --warn: #f0a83a; --bad: #f06a6a; --unknown: #7d889b;
    --shadow: 0 1px 2px rgba(0, 0, 0, .3), 0 4px 16px rgba(0, 0, 0, .25);
  }
}
* { box-sizing: border-box; }
[hidden] { display: none !important; }
body { margin: 0; background: var(--bg); color: var(--text); font: 15px/1.45 "Segoe UI", system-ui, -apple-system, sans-serif; }
.bar { position: sticky; top: 0; z-index: 5; display: flex; align-items: center; gap: 18px; padding: 12px 22px;
  background: var(--panel); border-bottom: 1px solid var(--line); }
.brand { white-space: nowrap; font-weight: 700; letter-spacing: .04em; font-size: 17px; }
.star { color: var(--accent); }
.dim { color: var(--dim); font-weight: 400; }
.where { color: var(--dim); font-size: 13px; flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.sep { margin: 0 8px; }
.status { display: flex; align-items: center; gap: 8px; font-size: 13px; color: var(--dim); }
.dot { width: 9px; height: 9px; border-radius: 50%; background: var(--unknown); }
.dot.ok { background: var(--ok); } .dot.bad { background: var(--bad); }
button { white-space: nowrap; font: inherit; border-radius: 8px; padding: 8px 14px; border: 1px solid var(--line); background: var(--panel); color: var(--text); cursor: pointer; }
button.primary { background: var(--accent); border-color: var(--accent); color: var(--accent-text); font-weight: 600; }
button.danger { background: var(--bad); border-color: var(--bad); color: #fff; font-weight: 600; }
button.ghost { background: transparent; }
button:hover { filter: brightness(1.05); }
main { padding: 18px 22px 60px; max-width: 1400px; margin: 0 auto; }
.tools { display: flex; gap: 14px; align-items: center; flex-wrap: wrap; margin-bottom: 14px; }
.chips { display: flex; gap: 8px; flex-wrap: wrap; flex: 1; }
.chip { display: inline-flex; gap: 6px; align-items: center; padding: 5px 12px; border-radius: 999px; background: var(--panel);
  border: 1px solid var(--line); font-size: 13px; cursor: pointer; user-select: none; }
.chip.on { border-color: var(--accent); box-shadow: inset 0 0 0 1px var(--accent); }
.chip b { font-size: 14px; }
input, select { font: inherit; color: var(--text); background: var(--panel); border: 1px solid var(--line); border-radius: 8px; padding: 8px 10px; }
#search { width: 240px; }
.message { padding: 12px 14px; border-radius: 10px; background: color-mix(in srgb, var(--bad) 14%, var(--panel)); border: 1px solid var(--bad); margin-bottom: 14px; }
.group { margin: 22px 0 10px; display: flex; align-items: baseline; gap: 10px; }
.group h3 { margin: 0; font-size: 15px; text-transform: uppercase; letter-spacing: .06em; }
.grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(260px, 1fr)); gap: 12px; }
.pc { background: var(--panel); border: 1px solid var(--line); border-left: 5px solid var(--unknown); border-radius: 12px; padding: 12px 14px;
  box-shadow: var(--shadow); cursor: pointer; transition: transform .08s; }
.pc:hover { transform: translateY(-1px); }
.pc:focus-visible, .chip:focus-visible, button:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; }
.pc.m-ok { border-left-color: var(--ok); } .pc.m-warn { border-left-color: var(--warn); } .pc.m-bad { border-left-color: var(--bad); }
.pc .top { display: flex; justify-content: space-between; align-items: center; gap: 8px; }
.pc .name { font-weight: 700; font-size: 16px; }
.mark { font-weight: 700; width: 24px; height: 24px; display: inline-grid; place-items: center; border-radius: 50%; color: #fff; font-size: 13px; background: var(--unknown); }
.mark.m-ok { background: var(--ok); } .mark.m-warn { background: var(--warn); } .mark.m-bad { background: var(--bad); }
.pc ul { margin: 8px 0 6px; padding: 0; list-style: none; font-size: 13px; }
.pc li { margin: 2px 0; }
.pc .hw { font-size: 12px; color: var(--dim); }
.empty { color: var(--dim); text-align: center; margin-top: 60px; }
.detail { position: fixed; top: 0; right: 0; bottom: 0; width: min(560px, 100%); background: var(--panel); border-left: 1px solid var(--line);
  box-shadow: -8px 0 30px rgba(0, 0, 0, .15); overflow-y: auto; z-index: 10; padding: 18px 22px 40px; }
.detail-head { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
.detail h2 { margin: 0 0 2px; }
.detail .now { margin-top: 12px; }
.detail h4 { margin: 20px 0 8px; font-size: 13px; text-transform: uppercase; letter-spacing: .06em; color: var(--dim); }
.kv { display: grid; grid-template-columns: 140px 1fr; gap: 4px 12px; font-size: 14px; }
.kv div:nth-child(odd) { color: var(--dim); }
.meter { height: 8px; background: var(--line); border-radius: 99px; overflow: hidden; margin-top: 4px; }
.meter span { display: block; height: 100%; background: var(--accent); }
.meter.full span { background: var(--bad); }
.entry { border: 1px solid var(--line); border-radius: 10px; padding: 8px 12px; margin-bottom: 8px; font-size: 13px; }
.entry.bad { border-color: color-mix(in srgb, var(--bad) 60%, var(--line)); }
.entry .when { color: var(--dim); margin-right: 8px; }
.entry ul { margin: 6px 0 0 16px; padding: 0; color: var(--bad); }
.entry .note { color: var(--dim); margin-top: 3px; }
.modal { position: fixed; inset: 0; background: rgba(10, 15, 25, .45); display: grid; place-items: center; z-index: 20; padding: 16px; }
.card { background: var(--panel); border-radius: 14px; padding: 22px 24px; width: min(520px, 100%); box-shadow: var(--shadow); }
.card h2 { margin-top: 0; }
.card label { display: flex; flex-direction: column; gap: 4px; font-size: 13px; color: var(--dim); margin: 12px 0; }
.card .row { display: flex; gap: 14px; } .card .row label { flex: 1; }
.actions { display: flex; justify-content: flex-end; gap: 10px; margin-top: 18px; }
.code { font: 700 34px/1.2 ui-monospace, "Cascadia Mono", Consolas, monospace; letter-spacing: .08em; text-align: center; padding: 16px;
  border: 2px dashed var(--accent); border-radius: 12px; margin: 8px 0; white-space: nowrap; }
.command { display: flex; gap: 8px; align-items: center; background: var(--bg); border: 1px solid var(--line); border-radius: 10px; padding: 8px 10px; }
.command code { flex: 1; font-size: 12px; word-break: break-all; }
.toast { position: fixed; bottom: 22px; left: 50%; transform: translateX(-50%); background: var(--text); color: var(--bg); padding: 10px 16px;
  border-radius: 10px; z-index: 30; box-shadow: var(--shadow); }
@media (max-width: 640px) { .where, .brand .dim, #status { display: none; } .status { margin-left: auto; } .code { font-size: 24px; } .kv { grid-template-columns: 110px 1fr; } #search { width: 100%; } .bar { gap: 10px; padding: 10px 14px; } main { padding: 12px 14px 60px; } }
""";

    public const string Script = """
"use strict";
(() => {
  const $ = id => document.getElementById(id);
  const marks = { "✓": "m-ok", "!": "m-warn", "✗": "m-bad", "?": "m-unknown" };
  const titles = { "✓": "v poriadku", "!": "pozrieť", "✗": "neozvalo sa", "?": "bez hlásenia" };
  let token = location.hash.slice(1);
  try {
    // the key of this run stays for other tabs of this browser too (it is worth nothing after the console stops)
    if (token) { localStorage.setItem("polaris-konzola", token); history.replaceState(null, "", "/"); }
    else { token = localStorage.getItem("polaris-konzola") || ""; }
  } catch (e) { /* the key of this run stays in memory only */ }
  let state = { pcs: [] }, filter = "", search = "", open = null;

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text !== undefined) e.textContent = text; return e; };
  const time = iso => new Date(iso).toLocaleString("sk-SK", { day: "numeric", month: "numeric", hour: "2-digit", minute: "2-digit" });
  const toast = text => { const t = $("toast"); t.textContent = text; t.hidden = false; clearTimeout(toast.t); toast.t = setTimeout(() => t.hidden = true, 3200); };

  async function ask(path, body) {
    const response = await fetch(path, {
      method: body ? "POST" : "GET",
      headers: Object.assign({ "X-Polaris-Konzola": token }, body ? { "Content-Type": "application/json" } : {}),
      body: body ? JSON.stringify(body) : undefined,
      cache: "no-store",
    });
    return response.json();
  }

  async function refresh() {
    try {
      const answer = await ask("/api/stav");
      $("console").textContent = answer.console ? "konzola " + answer.console : "";
      $("centre").textContent = answer.centre || "";
      $("dot").className = "dot " + (answer.ok ? "ok" : "bad");
      $("status").textContent = answer.ok ? "obnovené " + new Date().toLocaleTimeString("sk-SK", { hour: "2-digit", minute: "2-digit" }) : "centrum neodpovedá";
      const message = $("message");
      message.hidden = answer.ok;
      message.textContent = answer.ok ? "" : answer.message;
      if (answer.ok) { state = answer; draw(); }
    } catch (e) {
      $("dot").className = "dot bad";
      $("status").textContent = "konzola nebeží";
    }
  }

  function draw() {
    const counts = { "✓": 0, "!": 0, "✗": 0, "?": 0 };
    state.pcs.forEach(p => counts[p.mark]++);
    const chips = $("chips"); chips.replaceChildren();
    const all = el("span", "chip" + (filter === "" ? " on" : ""));
    all.append(el("b", "", String(state.pcs.length)), " všetky");
    all.onclick = () => { filter = ""; draw(); };
    chips.append(all);
    Object.keys(counts).forEach(m => {
      if (!counts[m]) return;
      const chip = el("span", "chip" + (filter === m ? " on" : ""));
      chip.append(el("span", "mark " + marks[m], m), el("b", "", String(counts[m])), " " + titles[m]);
      chip.onclick = () => { filter = filter === m ? "" : m; draw(); };
      chips.append(chip);
    });

    const groups = $("groups"); groups.replaceChildren();
    const list = $("m-groups"); list.replaceChildren();
    const byGroup = new Map();
    state.pcs.filter(p => (!filter || p.mark === filter) && (!search || p.name.toLowerCase().includes(search)))
      .forEach(p => { if (!byGroup.has(p.group)) byGroup.set(p.group, []); byGroup.get(p.group).push(p); });
    new Set(state.pcs.map(p => p.group)).forEach(g => list.append(Object.assign(el("option"), { value: g })));
    $("empty").hidden = state.pcs.length > 0;
    byGroup.forEach((pcs, group) => {
      const head = el("div", "group");
      head.append(el("h3", "", group), el("span", "dim", pcs.length + " PC"));
      const grid = el("div", "grid");
      pcs.forEach(p => grid.append(card(p)));
      groups.append(head, grid);
    });
    if (open) { const p = state.pcs.find(x => x.id === open); if (p) detail(p); }
  }

  function card(p) {
    const c = el("div", "pc " + marks[p.mark]);
    const top = el("div", "top");
    top.append(el("span", "name", p.name), el("span", "mark " + marks[p.mark], p.mark));
    const ul = el("ul");
    p.parts.forEach(x => ul.append(el("li", "", x)));
    c.append(top, ul, el("div", "hw", p.hardware + " · " + p.windows));
    c.tabIndex = 0;
    c.onclick = () => { open = p.id; detail(p); };
    c.onkeydown = e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); c.onclick(); } };
    return c;
  }

  function kv(rows) {
    const d = el("div", "kv");
    rows.forEach(([k, v]) => { if (v === undefined || v === "") return; d.append(el("div", "", k), typeof v === "string" ? el("div", "", v) : v); });
    return d;
  }

  function detail(p) {
    $("detail").hidden = false;
    $("d-name").textContent = p.name;
    $("d-sub").textContent = "skupina " + p.group + " · " + p.id.slice(0, 8) + " · zaregistrované " + time(p.registered);
    const body = $("d-body"); body.replaceChildren();
    const status = el("div", "now");
    status.append(el("span", "mark " + marks[p.mark], p.mark), " " + p.parts.join(" · "));
    body.append(status);
    let disk;
    if (p.totalDisk) {
      disk = el("div", "");
      disk.append(p.freeDisk + " voľné z " + p.totalDisk);
      const meter = el("div", "meter" + (p.diskUsed >= 90 ? " full" : ""));
      const bar = el("span"); bar.style.width = p.diskUsed + "%";
      meter.append(bar); disk.append(meter);
    }
    body.append(el("h4", "", "PC"), kv([
      ["Model", p.hardware], ["Windows", p.windows], ["Agent", p.agent], ["Profil", p.profile || "–"],
      ["Zapnuté", p.uptime], ["Disk", disk], ["Bez ovládača", p.devicesWithoutDriver ? String(p.devicesWithoutDriver) : "0"],
      ["Trezor", p.trezor || "–"], ["Hlásenie", p.received ? time(p.received) + " (pred " + p.ago + ")" : "zatiaľ nie"],
    ]));
    if (p.measurements && p.measurements.length) {
      body.append(el("h4", "", "Merania prihlásenia"));
      p.measurements.forEach(m => {
        const e = el("div", "entry");
        e.append(el("span", "when", time(m.time)));
        m.lines.forEach((l, i) => e.append(i ? el("div", "note", l) : el("span", "", l)));
        body.append(e);
      });
    }
    if (p.journal && p.journal.length) {
      body.append(el("h4", "", "Denník"));
      p.journal.forEach(j => {
        const e = el("div", "entry" + (j.failed ? " bad" : ""));
        e.append(el("span", "when", time(j.time)), el("b", "", j.recipe + " "), el("span", "dim", j.summary));
        if (j.note) e.append(el("div", "note", j.note));
        if (j.lines.length) { const ul = el("ul"); j.lines.forEach(l => ul.append(el("li", "", l))); e.append(ul); }
        body.append(e);
      });
    }
    const remove = el("button", "danger", "Odobrať PC z centra");
    remove.style.marginTop = "24px";
    remove.onclick = () => ask_remove(p);
    body.append(remove);
  }

  function ask_remove(p) {
    $("c-title").textContent = "Odobrať " + p.name + "?";
    $("c-text").textContent = "PC zmizne z centra a jeho hlásenia sa odmietnu. Späť ho pridáš novým kódom.";
    $("confirm").hidden = false;
    $("c-no").onclick = () => $("confirm").hidden = true;
    $("c-yes").onclick = async () => {
      $("confirm").hidden = true;
      const answer = await ask("/api/odobrat", { id: p.id });
      toast(answer.message || (answer.ok ? "Odobraté." : "Nepodarilo sa."));
      if (answer.ok) { open = null; $("detail").hidden = true; refresh(); }
    };
  }

  $("d-close").onclick = () => { open = null; $("detail").hidden = true; };
  $("search").oninput = e => { search = e.target.value.trim().toLowerCase(); draw(); };
  $("add").onclick = () => { $("m-form").hidden = false; $("m-result").hidden = true; $("modal").hidden = false; $("m-group").focus(); };
  $("m-cancel").onclick = () => $("modal").hidden = true;
  $("r-done").onclick = () => $("modal").hidden = true;
  $("m-go").onclick = async () => {
    const go = $("m-go"); go.disabled = true;
    try {
      const answer = await ask("/api/kod", { group: $("m-group").value, days: Number($("m-days").value), count: Number($("m-count").value) });
      if (!answer.ok) { toast(answer.message); return; }
      $("r-group").textContent = answer.group;
      $("r-code").textContent = answer.code;
      $("r-until").textContent = "Platí do " + time(answer.until) + ", najviac " + answer.count + " PC." + (answer.message ? " " + answer.message : "");
      $("r-command").textContent = answer.command;
      $("r-consoles").textContent = "PC budú dôverovať konzolám: " + answer.consoles.join(", ") + ".";
      $("m-form").hidden = true; $("m-result").hidden = false;
    } finally { go.disabled = false; }
  };
  $("r-copy").onclick = async () => {
    try { await navigator.clipboard.writeText($("r-command").textContent); toast("Príkaz skopírovaný."); }
    catch (e) { toast("Označ príkaz a skopíruj ho (Ctrl+C)."); }
  };
  document.addEventListener("keydown", e => { if (e.key === "Escape") { $("modal").hidden = true; $("confirm").hidden = true; if (open) { open = null; $("detail").hidden = true; } } });

  if (!token) {
    const m = $("message"); m.hidden = false;
    m.textContent = "Konzolu otvor adresou http://127.0.0.1:8444/#…, ktorú vypísalo okno konzola.cmd (skopíruj celú, aj časť za #).";
    return;
  }
  refresh();
  setInterval(refresh, 30000);
})();
""";
}
