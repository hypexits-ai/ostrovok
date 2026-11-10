'use strict';
/* Островок — the island itself: size spring, hover, wheel, windows, media, volume, events, drag to trash.
   The split-off cells live in cells.js (window.Cells). */

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const host = window.chrome && window.chrome.webview;
const send = m => { if (host) host.postMessage(m); };
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const ic = (g, c = '') => `<svg class="ic ${c}"><use href="#g-${g}"/></svg>`;
const reduce = matchMedia('(prefers-reduced-motion: reduce)').matches;
// page errors go to %LOCALAPPDATA%\Ostrovok\log.txt
window.addEventListener('error', e => send({ t: 'log', msg: `${e.message} @ ${e.filename}:${e.lineno}` }));
window.addEventListener('unhandledrejection', e => send({ t: 'log', msg: 'promise: ' + (e.reason && (e.reason.stack || e.reason.message) || e.reason) }));

const island = $('#island'), col = $('#col'), exp = $('#exp'), evt = $('#evt');
const gMain = $('#gMain'), gTrash = $('#gTrash'), trash = $('#trash'), ghost = $('#ghost');
const EXP = [720, 340, 44];
const EVT = { vol: [340, 56], batt: [410, 80], track: [430, 72], timer: [360, 72], msg: [460, 84], tg: [330, 60] };
const S = {
  hover: false, ev: null, mediaDismissed: false, trashSplit: false,
  media: { has: false, playing: false }, favs: [], wins: [],
  sys: { temp: null, load: 0, batt: null, charging: true }, vol: { v: .5, mute: false }, battWarned: false,
  config: { wifi: false, city: null },
  timer: { total: 300, left: 300, running: false, endAt: 0 },
  tg: { unread: -1, lastMsgAt: 0, burst: 0 },
  sw: { ms: 0, running: false, startAt: 0, laps: [] }
};
let drag = null, suppressClick = false;

/* ───────── helpers ───────── */
const setB = (k, v) => $$(`[data-b="${k}"]`).forEach(el => { if (el.textContent !== v) el.textContent = v; });
function appIcon(icon, name, size) {
  if (icon) return `<span class="app" style="--s:${size}px"><img src="${icon}" alt=""></span>`;
  return `<span class="app letter" style="--s:${size}px">${esc((name || '?').trim().charAt(0).toUpperCase())}</span>`;
}
const fmtTime = s => { s = Math.max(0, Math.floor(s)); return Math.floor(s / 60) + ':' + String(s % 60).padStart(2, '0'); };

/* ───────── clock ───────── */
function clock() {
  const now = new Date();
  setB('time', now.toLocaleTimeString('ru-RU', { hour: '2-digit', minute: '2-digit' }));
  setB('date', now.toLocaleDateString('ru-RU', { weekday: 'long', day: 'numeric', month: 'long' }));
  setTimeout(clock, 1000 - (Date.now() % 1000) + 5);
}

/* ───────── goo layer on/off (only while something splits) ───────── */
const Goo = {
  users: new Set(), timer: 0,
  need(name, on) {
    clearTimeout(this.timer);
    if (on) { this.users.add(name); document.body.classList.add('goo-on'); }
    else {
      this.users.delete(name);
      if (!this.users.size) this.timer = setTimeout(() => { if (!this.users.size) document.body.classList.remove('goo-on'); postRect(); }, 650);
    }
    postRect();
  }
};

/* ───────── island size (spring) ───────── */
const cur = { w: 280, h: 36, r: 18 }, vel = { w: 0, h: 0, r: 0 };
let tgt = { ...cur }, spring = { k: 260, c: 26 }, running = false, last = 0;

function mediaShown() { return !!S.media.has && !S.mediaDismissed; }
function collapsedWidth() {
  const gpu = S.sys.gpu !== null && S.sys.gpu !== undefined;
  return (mediaShown() || S.timer.running ? 330 : 280) + (S.tg.unread > 0 ? 44 : 0) + (gpu ? 30 : 0);
}
function visual() { if (S.hover) return 'expanded'; if (S.ev) return 'event'; if (S.idle) return 'mini'; return 'collapsed'; }
// the mini layer sizes itself to its content (time, equalizer, CPU, GPU); the island follows it
function miniWidth() { return Math.max(96, Math.ceil($('#mini').offsetWidth)); }

function update(snap) {
  const vs = visual();
  island.dataset.state = vs;
  const cw = collapsedWidth(), mw = miniWidth();
  col.style.width = cw + 'px';
  exp.style.width = EXP[0] + 'px'; exp.style.height = EXP[1] + 'px';
  const d = vs === 'expanded' ? EXP : vs === 'event' ? [EVT[S.ev.kind][0], EVT[S.ev.kind][1], 38] : vs === 'mini' ? [mw, 28, 14] : [cw, 36, 18];
  tgt = { w: d[0], h: d[1], r: d[2] };
  spring = tgt.h > cur.h + 1 ? { k: 250, c: 25 } : { k: 420, c: 38 };
  $('#hot').style.width = (vs === 'mini' ? mw : cw) + 'px';
  if (snap || reduce) { Object.assign(cur, tgt); vel.w = vel.h = vel.r = 0; apply(); }
  kick();
}

function apply() {
  const h = Math.max(1, cur.h), r = Math.max(0, Math.min(cur.r, h / 2));
  island.style.width = Math.max(1, cur.w).toFixed(2) + 'px';
  island.style.height = h.toFixed(2) + 'px';
  island.style.borderRadius = r.toFixed(2) + 'px';
  gMain.style.width = island.style.width; gMain.style.height = island.style.height; gMain.style.borderRadius = island.style.borderRadius;
  postRect();
}

let lastRect = '';
function postRect() {
  let rects;
  if ((drag && drag.active) || S.trashSplit) rects = [{ x: 0, y: 0, w: innerWidth, h: innerHeight }];
  else {
    const W = Math.max(cur.w, tgt.w), H = Math.max(cur.h, tgt.h);
    rects = [{ x: Math.floor((innerWidth - W) / 2 - 6), y: 0, w: Math.ceil(W + 12), h: Math.ceil(8 + H + 8) }];
    const c = typeof Cells !== 'undefined' ? Cells.rect() : null;
    if (c) rects.push(c);
  }
  const key = JSON.stringify(rects);
  if (key !== lastRect) { lastRect = key; send({ t: 'rect', rects }); }
}

function kick() { if (!running) { running = true; last = performance.now(); requestAnimationFrame(loop); } }
function loop(t) {
  const dt = Math.min(.034, Math.max(0, (t - last) / 1000)); last = t;
  let moving = false; const n = 4, h = dt / n;
  for (const k of ['w', 'h', 'r']) {
    let x = cur[k], v = vel[k]; const T = tgt[k];
    if (Math.abs(x - T) < .05 && Math.abs(v) < .05) { if (x !== T) { cur[k] = T; moving = true; } vel[k] = 0; continue; }
    for (let i = 0; i < n; i++) { const a = -spring.k * (x - T) - spring.c * v; v += a * h; x += v * h; }
    cur[k] = x; vel[k] = v; moving = true;
  }
  if (moving) apply(); else postRect();
  const wheelMoving = wheel.step(dt);
  if (wheelMoving) wheel.render();
  if (moving || wheelMoving) requestAnimationFrame(loop); else running = false;
}

/* ───────── hover ───────── */
let hovTimer = 0;
const isMessage = () => S.ev && (S.ev.kind === 'msg' || S.ev.kind === 'tg');
// after a while without the pointer the island shrinks to its smallest form
S.lastActive = Date.now();
function touchActive() { S.lastActive = Date.now(); if (S.idle) { S.idle = false; update(); } }
setInterval(() => {
  const limit = (S.config.idleMinutes ?? 5) * 60000;
  if (!S.idle && !S.hover && limit > 0 && Date.now() - S.lastActive > limit && !(typeof Cells !== 'undefined' && Cells.isOpen())) { S.idle = true; update(); }
}, 5000);
function enter() {
  clearTimeout(hovTimer);
  touchActive();
  // over a message: keep it on screen so it can be read and clicked, instead of unfolding the island
  if (isMessage()) { S.evHold = true; clearTimeout(evTimer); return; }
  if (!S.hover) { S.hover = true; update(); }
}
function leave(delay = 280) {
  clearTimeout(hovTimer);
  S.lastActive = Date.now();
  if (S.evHold) { S.evHold = false; clearTimeout(evTimer); evTimer = setTimeout(() => { S.ev = null; update(); }, 1500); return; }
  hovTimer = setTimeout(() => {
    if ((drag && drag.active) || Cells.holdsFocus()) return;
    if (Cells.isOpen()) { Cells.close(); hovTimer = setTimeout(() => { S.hover = false; update(); }, 220); return; }
    S.hover = false; update();
  }, delay);
}
const busy = () => (drag && drag.active);
island.addEventListener('pointerenter', enter);
island.addEventListener('pointerleave', () => { if (!busy()) leave(); });
$('#hot').addEventListener('pointerenter', enter);
$('#hot').addEventListener('pointerleave', e => { if (!island.contains(e.relatedTarget) && !busy()) leave(); });
$('#cell').addEventListener('pointerenter', enter);
$('#cell').addEventListener('pointerleave', () => { if (!busy() && !Cells.holdsFocus()) leave(); });

/* ───────── favourites wheel ───────── */
const wheel = {
  root: $('#wheel'), box: $('#wheel .w-items'), items: [], n: 0, pos: 0, target: 0, acc: 0, drag: null,
  set(list) {
    const keep = this.items.length ? S.favs[this.sel()]?.id : null;
    this.box.innerHTML = '';
    this.items = list.map((a, i) => {
      const b = document.createElement('button');
      b.className = 'w-it'; b.title = a.name;
      b.innerHTML = appIcon(a.icon, a.name, 28) + `<span>${esc(a.name)}</span>`;
      b.addEventListener('click', () => { if (this.drag && this.drag.moved) return; this.pick(i); });
      this.box.append(b); return b;
    });
    this.n = list.length;
    $('#wheel .w-empty').hidden = this.n > 0;
    $('#wheel .w-deco').hidden = this.n === 0;
    const idx = Math.max(0, list.findIndex(a => a.id === keep));
    this.pos = this.target = idx;
    this.render();
  },
  wrap(d) { const n = this.n || 1; d = ((d % n) + n) % n; return d > n / 2 ? d - n : d; },
  sel() { const n = this.n || 1; return ((Math.round(this.target) % n) + n) % n; },
  pick(i) { const off = this.wrap(i - this.target); if (Math.abs(off) < .5) this.launch(); else { this.target += off; kick(); } },
  launch() {
    if (!this.n) return;
    const i = this.sel(), el = this.items[i];
    el.classList.remove('launch'); void el.offsetWidth; el.classList.add('launch');
    send({ t: 'launch', id: S.favs[i].id });
  },
  step(dt) {
    const d = this.target - this.pos;
    if (Math.abs(d) < .001) { if (this.pos !== this.target) { this.pos = this.target; return true; } return false; }
    this.pos += d * (1 - Math.exp(-dt * (reduce ? 80 : 11))); return true;
  },
  render() {
    // a drum: items further from the centre turn away, shrink and fade faster
    this.items.forEach((el, i) => {
      const off = this.wrap(i - this.pos), ao = Math.abs(off), a = off * .48;
      const s = 1 - Math.min(.3, ao * .1);
      el.style.transform = `translate(-50%,-50%) translateY(${(Math.sin(a) * 84).toFixed(2)}px) translateZ(${((Math.cos(a) - 1) * 40).toFixed(2)}px) rotateX(${(-a * 57.3).toFixed(2)}deg) scale(${s.toFixed(3)})`;
      const o = ao > 3 ? 0 : Math.max(0, 1 - ao * .36);
      el.style.opacity = o.toFixed(3); el.style.zIndex = 100 - Math.round(ao * 10);
      el.style.pointerEvents = o < .05 ? 'none' : '';
      el.classList.toggle('cur', ao < .5);
    });
  }
};
wheel.root.addEventListener('wheel', e => {
  e.preventDefault(); if (!wheel.n) return;
  const d = Math.abs(e.deltaY) >= Math.abs(e.deltaX) ? e.deltaY : e.deltaX;
  const step = e.deltaMode === 1 ? 3 : 50; wheel.acc += d;
  while (Math.abs(wheel.acc) >= step) { const s = Math.sign(wheel.acc); wheel.target += s; wheel.acc -= s * step; }
  kick();
}, { passive: false });
wheel.root.addEventListener('pointerdown', e => { if (e.button === 0) wheel.drag = { y: e.clientY, acc: 0, moved: false, id: e.pointerId }; });
wheel.root.addEventListener('pointermove', e => {
  const g = wheel.drag; if (!g || !wheel.n) return;
  g.acc += -(e.clientY - g.y); g.y = e.clientY;
  if (!g.moved && Math.abs(g.acc) > 6) { g.moved = true; try { wheel.root.setPointerCapture(g.id); } catch (_) {} }
  while (Math.abs(g.acc) >= 30) { const s = Math.sign(g.acc); wheel.target += s; g.acc -= s * 30; }
  kick();
});
['pointerup', 'pointercancel'].forEach(n => wheel.root.addEventListener(n, () => setTimeout(() => { wheel.drag = null; }, 0)));

/* ───────── open windows ───────── */
const closing = new Map();
let winsPending = false;
function renderWins() {
  if (drag && drag.active) { winsPending = true; return; }
  winsPending = false;
  const now = Date.now();
  const wins = S.wins.filter(w => !(closing.get(w.id) > now));
  $('#wins').innerHTML = wins.length ? wins.map(w =>
    `<button class="wrow${w.active ? ' act' : ''}" data-win="${w.id}" data-drag="win" title="${esc(w.title)}">${appIcon(w.icon, w.name, 28)}<div><b>${esc(w.name)}</b></div></button>`
  ).join('') : '<div class="wl-empty">Нет открытых окон</div>';
  const seen = new Set(), icons = [];
  for (const w of [...wins.filter(w => w.active), ...wins]) {
    const key = w.icon || w.name; if (seen.has(key)) continue; seen.add(key);
    icons.push(appIcon(w.icon, w.name, 18)); if (icons.length === 3) break;
  }
  $('#c-apps').innerHTML = icons.join('');
}
$('#wins').addEventListener('click', e => {
  const b = e.target.closest('[data-win]'); if (!b || suppressClick) return;
  send({ t: 'activate', id: b.dataset.win });
});

/* ───────── media ───────── */
function onMedia(m) {
  const prev = S.media;
  const changedTrack = m.has && m.playing && (!prev.has || prev.title !== m.title || prev.artist !== m.artist);
  if (S.mediaDismissed && (!m.has || m.title !== prev.title || (m.playing && !prev.playing))) S.mediaDismissed = false;
  m.at = performance.now();
  S.media = m;
  applyMedia();
  if (changedTrack && !S.hover && prev.title !== undefined && !S.mediaDismissed) showEvent({ kind: 'track' });
  Cells.onMedia();
  update();
}
function mediaPos() {
  const m = S.media; if (!m.dur) return 0;
  return Math.min(m.dur, (m.pos || 0) + (m.playing ? (performance.now() - m.at) / 1000 : 0));
}
function applyMedia() {
  const m = S.media;
  document.body.dataset.media = mediaShown() ? '1' : '0';
  document.body.dataset.playing = mediaShown() && m.playing ? '1' : '0';
  setB('trk', m.title || ''); setB('art', m.artist || '');
  $$('.hm-a').forEach(el => el.style.display = m.artist ? '' : 'none');
  $$('[data-cover]').forEach(el => el.style.backgroundImage = m.art ? `url("${m.art}")` : '');
  $('#b-next').disabled = !m.canNext;
}
let level = 0;
function onLevel(v) {
  level = Math.max(v, level * .82);
  const lv = Math.min(1, .25 + level * 1.4).toFixed(2);
  $$('.eq').forEach(el => el.style.setProperty('--lv', lv));
}

/* ───────── system ───────── */
function renderSys() {
  const { temp, load } = S.sys;
  const hasTemp = temp !== null && temp !== undefined;
  const v = hasTemp ? temp : load;
  const color = hasTemp ? (temp < 60 ? '#5be38f' : temp < 75 ? '#ffc145' : '#ff5a5a') : (load < 50 ? '#5be38f' : load < 80 ? '#ffc145' : '#ff5a5a');
  document.documentElement.style.setProperty('--tc', color);
  setB('chip', hasTemp ? temp + '°' : load + '%');
  $('#c-ic use').setAttribute('href', hasTemp ? '#g-therm' : '#g-cpu');
  setB('g-main', hasTemp ? temp + '°' : load + '%');
  setB('g-sub', hasTemp ? 'ЦП ' + load + '%' : 'загрузка ЦП');
  $$('.cpu-mini:not(.gpu-mini) .g-val').forEach(el => el.style.strokeDashoffset = (163.4 * (1 - Math.min(100, v) / 100)).toFixed(1));
  setB('cpu-pct', (hasTemp ? temp + '° ' : '') + load + '%');
  // graphics card
  const gpu = S.sys.gpu, hasGpu = gpu !== null && gpu !== undefined;
  $('#gpu-mini').hidden = !hasGpu; $('#mini .m-gpu').hidden = !hasGpu; $('#c-gpu').hidden = !hasGpu;
  if (hasGpu) {
    document.documentElement.style.setProperty('--gc', gpu < 50 ? '#5be38f' : gpu < 80 ? '#ffc145' : '#ff5a5a');
    setB('gpu-main', gpu + '%'); setB('gpu-pct', gpu + '%');
    $('#gpu-ring').style.strokeDashoffset = (163.4 * (1 - Math.min(100, gpu) / 100)).toFixed(1);
  }
  if (S.idle) update();
  const b = S.sys.batt;
  if (b !== null && b !== undefined) {
    if (!S.sys.charging && b <= 15 && !S.battWarned) { S.battWarned = true; showEvent({ kind: 'batt', pct: b }); }
    if (S.sys.charging || b > 20) S.battWarned = false;
  }
}

/* ───────── volume + toggles ───────── */
let dragging = false;
const volIn = $('#vol');
const paintRange = (el, p) => el.style.setProperty('--p', p + '%');
function paintVol(p) { paintRange(volIn, p); $('#vol-n').textContent = p; }
function onVol(m) {
  S.vol = m;
  const p = Math.round(m.v * 100);
  if (!dragging) { volIn.value = p; paintVol(p); }
  $('#t-mute').classList.toggle('on', !!m.mute);
  if (m.ext && !S.hover) showEvent({ kind: 'vol' });
  else if (S.ev && S.ev.kind === 'vol') renderEvent();
}
let volRaf = 0;
volIn.addEventListener('pointerdown', () => { dragging = true; });
volIn.addEventListener('input', () => {
  paintVol(volIn.value);
  if (!volRaf) volRaf = requestAnimationFrame(() => { volRaf = 0; send({ t: 'cmd', c: 'vol', v: volIn.value / 100 }); });
});
volIn.addEventListener('change', () => { dragging = false; send({ t: 'cmd', c: 'vol', v: volIn.value / 100 }); });
volIn.addEventListener('wheel', e => {
  e.preventDefault();
  volIn.value = Math.max(0, Math.min(100, +volIn.value + (e.deltaY < 0 ? 2 : -2)));
  paintVol(volIn.value); send({ t: 'cmd', c: 'vol', v: volIn.value / 100 });
}, { passive: false });

function onRadios(m) {
  S.radios = m;
  const wifiShown = (m.wifi !== null && m.wifi !== undefined) || S.config.wifi;
  $('#t-wifi').hidden = !wifiShown; $('#t-wifi').classList.toggle('on', !!m.wifi);
  $('#t-bt').hidden = m.bt === null || m.bt === undefined; $('#t-bt').classList.toggle('on', !!m.bt);
  Cells.onRadios();
}

/* ───────── timer & stopwatch (kept here so they run with the cell closed) ───────── */
let timerTick = 0;
function timerLeft() { const t = S.timer; return t.running ? Math.max(0, (t.endAt - Date.now()) / 1000) : t.left; }
function swMs() { const s = S.sw; return s.running ? s.ms + (Date.now() - s.startAt) : s.ms; }
function timerLoop() {
  const left = timerLeft();
  if (S.timer.running && left <= 0) {
    S.timer.running = false; S.timer.left = S.timer.total;
    beep(); showEvent({ kind: 'timer' });
  }
  for (const [chipSel, timeSel] of [['#c-timer', '#c-time'], ['#mini .m-timer', '#mini .m-clock']]) {
    const chip = $(chipSel);
    chip.hidden = !S.timer.running;
    $(timeSel).hidden = S.timer.running;
    if (S.timer.running) chip.querySelector('b').textContent = fmtTime(Math.ceil(left));
  }
  Cells.onTick();
  update();
  if (!S.timer.running && !S.sw.running) { clearInterval(timerTick); timerTick = 0; }
}
function ensureTicking() { if (!timerTick) timerTick = setInterval(timerLoop, 200); timerLoop(); }
function beep() {
  try {
    const ac = new AudioContext(), t = ac.currentTime;
    [0, .28, .56, 1.2, 1.48, 1.76].forEach(d => {
      const o = ac.createOscillator(), g = ac.createGain();
      o.type = 'sine'; o.frequency.value = 880;
      g.gain.setValueAtTime(.0001, t + d); g.gain.exponentialRampToValueAtTime(.25, t + d + .02); g.gain.exponentialRampToValueAtTime(.0001, t + d + .22);
      o.connect(g).connect(ac.destination); o.start(t + d); o.stop(t + d + .24);
    });
    setTimeout(() => ac.close(), 2500);
  } catch (_) {}
}

/* ───────── events (mini notifications) ───────── */
let evTimer = 0;
const plural = (n, one, few, many) => { const a = n % 100, b = n % 10; return a > 10 && a < 20 ? many : b === 1 ? one : b >= 2 && b <= 4 ? few : many; };
function showEvent(e) {
  S.ev = e; renderEvent();
  clearTimeout(evTimer);
  const ms = { vol: 1600, timer: 6000, msg: 5500, tg: 4000 }[e.kind] || 3600;
  evTimer = setTimeout(() => { S.ev = null; update(); }, ms);
  update();
}
// a click on a Telegram notification opens Telegram
evt.addEventListener('click', () => {
  if (!S.ev || (S.ev.kind !== 'msg' && S.ev.kind !== 'tg')) return;
  send({ t: 'tg.open' });
  clearTimeout(evTimer); S.ev = null; S.evHold = false; update();
});

/* ───────── Telegram ───────── */
function onTgMessage(m) {
  const now = Date.now();
  // several messages in a row: keep the newest on screen and count the rest
  S.tg.burst = S.ev && S.ev.kind === 'msg' && now - S.tg.lastMsgAt < 5000 ? S.tg.burst + 1 : 0;
  S.tg.lastMsgAt = now;
  showEvent({ kind: 'msg', name: m.name, text: m.text, avatar: m.avatar, more: S.tg.burst });
}
function onTgUnread(n) {
  const prev = S.tg.unread;
  S.tg.unread = n;
  const badge = $('#c-tg');
  badge.hidden = !(n > 0);
  badge.querySelector('b').textContent = n > 99 ? '99+' : n;
  update();
  // Telegram without Windows notifications: at least say that something new came in
  if (prev >= 0 && n > prev) setTimeout(() => { if (Date.now() - S.tg.lastMsgAt > 3000 && !S.hover) showEvent({ kind: 'tg', n }); }, 2000);
}
function renderEvent() {
  const e = S.ev; if (!e) return;
  evt.style.width = EVT[e.kind][0] + 'px'; evt.style.height = EVT[e.kind][1] + 'px';
  if (e.kind === 'vol') {
    const p = Math.round(S.vol.v * 100);
    evt.innerHTML = `<div class="ev ev-vol">${ic(S.vol.mute ? 'mute' : 'vol')}<div class="bar"><i style="width:${S.vol.mute ? 0 : p}%"></i></div><b>${S.vol.mute ? '—' : p}</b></div>`;
  } else if (e.kind === 'track') {
    const m = S.media;
    evt.innerHTML = `<div class="ev"><div class="cover" style="${m.art ? `background-image:url('${m.art}')` : ''}"></div><div class="ev-txt"><b>${esc(m.title)}</b><span>${esc(m.artist || 'Сейчас играет')}</span></div><span class="eq"><i></i><i></i><i></i><i></i><i></i></span></div>`;
  } else if (e.kind === 'msg') {
    const av = e.avatar ? `<span class="avatar" style="background-image:url('${e.avatar}')"></span>` : `<span class="avatar">${esc((e.name || '?').trim().charAt(0).toUpperCase())}</span>`;
    evt.innerHTML = `<div class="ev ev-msg">${av}<div class="ev-txt"><b>${esc(e.name)}${e.more ? `<em>и ещё ${e.more}</em>` : ''}</b><span>${esc(e.text || 'Новое сообщение')}</span></div><span class="tg-mark">${ic('send')}</span></div>`;
  } else if (e.kind === 'tg') {
    evt.innerHTML = `<div class="ev ev-tg"><span class="tg-mark">${ic('send')}</span><div class="ev-txt"><b>Telegram</b><span>${e.n} ${plural(e.n, 'непрочитанное', 'непрочитанных', 'непрочитанных')}</span></div></div>`;
  } else if (e.kind === 'timer') {
    evt.innerHTML = `<div class="ev ev-timer">${ic('timer')}<div class="ev-txt"><b>Таймер</b><span>Время вышло</span></div></div>`;
  } else {
    evt.innerHTML = `<div class="ev ev-batt"><div class="ev-txt"><b>Низкий заряд<em>${e.pct}%</em></b><span>Включите экономию энергии или поставьте ноутбук на зарядку</span></div><span class="battbig"></span></div>`;
  }
}

/* ───────── drag a window or the player into the trash ───────── */
let hot = false;
function splitTrash(on) {
  S.trashSplit = on;
  if (on) { Goo.need('trash', true); requestAnimationFrame(() => document.body.classList.add('split')); }
  else { document.body.classList.remove('split'); setHot(false, 0); Goo.need('trash', false); }
  postRect();
}
function trashRect() { return { x: innerWidth / 2 + 378, y: 135, w: 86, h: 86 }; }
function setHot(on, heat) {
  trash.style.setProperty('--heat', heat.toFixed(2));
  if (on === hot) return;
  hot = on;
  trash.classList.toggle('hot', on); gTrash.classList.toggle('hot', on); ghost.classList.toggle('over', on);
}
function dragInfo(src) {
  if (src.dataset.drag === 'player') {
    const m = S.media;
    const sub = m.browser ? 'музыка встанет на паузу' : `${m.app || 'программа'} закроется`;
    return { kind: 'player', html: `<div class="cover" style="${m.art ? `background-image:url('${m.art}')` : ''}"></div><div><b>${esc(m.title)}</b><span>${esc(sub)}</span></div>` };
  }
  const w = S.wins.find(x => x.id === src.dataset.win);
  if (!w) return null;
  return { kind: 'win', id: w.id, html: `${appIcon(w.icon, w.name, 28)}<div><b>${esc(w.name)}</b><span>${esc(w.title && w.title !== w.name ? w.title : 'закроется')}</span></div>` };
}
document.addEventListener('pointerdown', e => {
  if (e.button !== 0 || visual() !== 'expanded' || Cells.isOpen()) return;
  const src = e.target.closest('[data-drag]');
  if (!src || e.target.closest('.ib')) return;
  const info = dragInfo(src); if (!info) return;
  const r = src.getBoundingClientRect();
  drag = { src, ...info, sx: e.clientX, sy: e.clientY, off: { x: e.clientX - r.left, y: e.clientY - r.top }, origin: r, active: false, pid: e.pointerId };
  try { src.setPointerCapture(e.pointerId); } catch (_) {}
});
document.addEventListener('pointermove', e => {
  if (!drag) return;
  if (!drag.active) {
    if (Math.hypot(e.clientX - drag.sx, e.clientY - drag.sy) < 6) return;
    drag.active = true;
    drag.src.classList.add('lifted');
    ghost.innerHTML = drag.html; ghost.hidden = false;
    ghost.style.width = Math.max(220, Math.min(280, drag.origin.width)) + 'px';
    ghost.style.transformOrigin = `${drag.off.x}px ${drag.off.y}px`;
    ghost.style.transition = 'none'; ghost.style.opacity = '';
    splitTrash(true);
  }
  const c = trashRect(), cx = c.x + c.w / 2, cy = c.y + c.h / 2;
  const inside = e.clientX > c.x - 20 && e.clientX < c.x + c.w + 20 && e.clientY > c.y - 20 && e.clientY < c.y + c.h + 20;
  setHot(inside, inside ? 1 : Math.max(0, 1 - Math.hypot(e.clientX - cx, e.clientY - cy) / 320));
  if (!ghost.style.transition.includes('transform')) ghost.style.transition = 'transform .16s ease-out, box-shadow .2s';
  ghost.style.transform = `translate(${e.clientX - drag.off.x}px,${e.clientY - drag.off.y}px) scale(${inside ? .5 : 1.04}) rotate(${inside ? 0 : -2}deg)`;
});
function endDrag(e) {
  if (!drag) return;
  const d = drag; drag = null;
  try { d.src.releasePointerCapture(d.pid); } catch (_) {}
  if (!d.active) return;
  suppressClick = true; setTimeout(() => { suppressClick = false; }, 0);
  const dropped = hot;
  if (dropped) {
    const c = trashRect();
    ghost.style.transition = reduce ? 'none' : 'transform .32s cubic-bezier(.5,0,.75,0), opacity .32s';
    ghost.style.transform = `translate(${c.x + c.w / 2 - d.off.x}px,${c.y + c.h / 2 - d.off.y}px) scale(.08)`;
    ghost.style.opacity = '0';
    setTimeout(() => { trash.classList.remove('gulp'); void trash.offsetWidth; trash.classList.add('gulp'); setHot(false, 1); }, reduce ? 0 : 280);
    if (d.kind === 'win') {
      closing.set(d.id, Date.now() + 2500);
      setTimeout(renderWins, 2600);
      send({ t: 'close', id: d.id });
    } else {
      S.mediaDismissed = true; applyMedia();
      send({ t: 'closeMedia' });
    }
    setTimeout(() => splitTrash(false), reduce ? 150 : 900);
  } else {
    ghost.style.transition = reduce ? 'none' : 'transform .4s cubic-bezier(.3,1.3,.5,1)';
    ghost.style.transform = `translate(${d.origin.left}px,${d.origin.top}px) scale(1)`;
    splitTrash(false);
  }
  setTimeout(() => {
    ghost.hidden = true;
    $$('.lifted').forEach(el => el.classList.remove('lifted'));
    renderWins(); update();
  }, reduce ? 0 : 420);
  const over = document.elementFromPoint(e.clientX, e.clientY);
  if (!over || !island.contains(over)) leave(dropped ? 1100 : 450);
}
document.addEventListener('pointerup', endDrag);
document.addEventListener('pointercancel', endDrag);

/* ───────── buttons, right clicks ───────── */
island.addEventListener('click', e => {
  const c = e.target.closest('[data-click-cell]');
  if (c) { Cells.toggle(c.dataset.clickCell, c); return; }
  const b = e.target.closest('[data-act]'); if (!b) return;
  const a = b.dataset.act;
  switch (a) {
    case 'play':
      document.body.dataset.playing = document.body.dataset.playing === '1' ? '0' : '1';
      send({ t: 'cmd', c: 'play' }); break;
    case 'next': send({ t: 'cmd', c: 'next' }); break;
    case 'wifi': case 'bt': b.classList.toggle('on'); send({ t: 'cmd', c: a }); break;
    case 'mute': send({ t: 'cmd', c: 'mute' }); break;
    default: send({ t: 'cmd', c: a });
  }
});
document.addEventListener('contextmenu', e => {
  e.preventDefault();
  const t = e.target.closest('[data-cell]');
  if (t && island.contains(t) && visual() === 'expanded') Cells.toggle(t.dataset.cell, t);
});
document.addEventListener('keydown', e => { if (e.key === 'Escape') Cells.close(); });
// the user clicked another window while typing in a cell: put the cell away.
// Taking the keyboard itself makes the page blur and refocus for a moment, so wait and look again.
window.addEventListener('blur', () => {
  setTimeout(() => { if (Cells.hasKeys() && !document.hasFocus() && !Cells.justTookKeys()) Cells.close(false); }, 250);
});

/* ───────── host messages ───────── */
if (host) host.addEventListener('message', e => {
  const m = e.data;
  switch (m.t) {
    case 'favs': S.favs = m.list; wheel.set(m.list); Cells.onFavs(); break;
    case 'wins': S.wins = m.list; renderWins(); break;
    case 'media': onMedia(m); break;
    case 'level': onLevel(m.v); break;
    case 'sys': S.sys = m; renderSys(); break;
    case 'vol': onVol(m); break;
    case 'radios': onRadios(m); break;
    case 'config': S.config = m; onRadios(S.radios || {}); break;
    case 'tg': onTgMessage(m); break;
    case 'tgUnread': onTgUnread(m.n); break;
    case 'leave': if (S.hover && !busy() && !Cells.holdsFocus()) leave(120); break;
    default: Cells.onHost(m);
  }
});
