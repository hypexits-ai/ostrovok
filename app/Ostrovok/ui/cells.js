'use strict';
/* Островок — cells that split off the island: search, calendar, player, favourites,
   Bluetooth, Wi-Fi, sound, processor and power. A cell below the island takes its width,
   a cell to the side takes its height, and it grows out from under the button that opened it. */

const Cells = (() => {
  const cellEl = $('#cell'), gCell = $('#gCell');
  let cc = $('#cc');
  const GAP = 16;
  const ISL = () => ({ x: innerWidth / 2 - 360, y: islandTop(), w: 720, h: 340 });
  // [width, height, side]; 0 = take the island's size on that axis
  const SIZES = {
    search: [720, 360, 'below'], cal: [520, 0, 'left'], player: [720, 200, 'below'], favs: [320, 0, 'left'],
    bt: [720, 236, 'below'], wifi: [720, 300, 'below'], sound: [720, 316, 'below'], cpu: [720, 300, 'below'], power: [210, 0, 'right']
  };
  const BELOW_HEIGHT = { cal: 300, favs: 300, power: 150 };     // when the screen is too narrow for a side cell
  const WATCH = { bt: 'bt', wifi: 'wifi', sound: 'audio', cpu: 'procs', cal: 'weather' };
  const clamp = (v, a, b) => Math.max(a, Math.min(b, v));

  let current = null, srcEl = null, geo = null, handler = null, anims = [], visible = false, keys = false;

  /* ───── geometry & motion ───── */
  function geometry(kind, t) {
    const I = ISL(), tr = t.getBoundingClientRect();
    let [w, h, side] = SIZES[kind];
    if (side === 'left' && I.x - GAP - w < 8) side = 'below';
    if (side === 'right' && I.x + I.w + GAP + w > innerWidth - 8) side = 'below';
    const oy = clamp(tr.top + tr.height / 2 - I.y, 40, I.h - 40);
    if (side === 'left') return { side, wide: false, x: I.x - GAP - w, y: I.y, w, h: I.h, ox: w, oy, dx: GAP + w * .6, dy: 0 };
    if (side === 'right') return { side, wide: false, x: I.x + I.w + GAP, y: I.y, w, h: I.h, ox: 0, oy, dx: -(GAP + w * .6), dy: 0 };
    h = h || BELOW_HEIGHT[kind];
    return { side: 'below', wide: SIZES[kind][2] !== 'below', x: I.x, y: I.y + I.h + GAP, w: I.w, h, ox: tr.left + tr.width / 2 - I.x, oy: 0, dx: 0, dy: -(GAP + h * .5) };
  }
  function spring(from, to, k, c) {
    let x = from, v = 0, t = 0; const out = [], dt = 1 / 240;
    while (t < 2) {
      for (let i = 0; i < 4; i++) { const a = -k * (x - to) - c * v; v += a * dt; x += v * dt; t += dt; }
      out.push([x, v]);
      if (t > .15 && Math.abs(x - to) < .0015 && Math.abs(v) < .03) break;
    }
    out.push([to, 0]);
    return out;
  }
  function frames(g, series) {
    const vertical = g.side === 'below';
    return series.map(([p, v]) => {
      const s = .3 + .7 * p;
      const a = 1 + Math.max(-.05, Math.min(.06, v * .028)), b = 1 - (a - 1) * .6;
      const sx = vertical ? s * b : s * a, sy = vertical ? s * a : s * b;
      return { transform: `translate(${(g.dx * (1 - p)).toFixed(2)}px,${(g.dy * (1 - p)).toFixed(2)}px) scale(${sx.toFixed(4)},${sy.toFixed(4)})` };
    });
  }
  function play(g, series) {
    anims.forEach(a => a.cancel());
    const f = frames(g, series), duration = reduce ? 1 : f.length * 1000 / 60;
    anims = [cellEl, gCell].map(el => el.animate(f, { duration, easing: 'linear', fill: 'forwards' }));
    return anims[0].finished.catch(() => {});
  }
  function recoil(side, amp, delay) {
    if (reduce) return;
    const f = [], axis = side === 'below' ? 'scaleY' : 'scaleX';
    for (let i = 0; i <= 60; i++) {
      const t = i / 60 - delay, d = Math.max(0, t);
      const s = 1 + (t <= 0 ? 0 : amp * Math.exp(-d * 6.5) * Math.sin(d * 2 * Math.PI * 3.1));
      f.push({ transform: `translateX(-50%) ${axis}(${s.toFixed(5)})` });
    }
    f.push({ transform: 'translateX(-50%)' });
    const origin = side === 'below' ? '50% 0' : side === 'left' ? '0% 50%' : '100% 50%';
    for (const el of [island, gMain]) { el.style.transformOrigin = origin; el.animate(f, { duration: f.length * 1000 / 60, easing: 'linear' }); }
  }

  /* ───── keyboard focus (the island never takes it unless a cell needs typing) ───── */
  let keysAt = 0;
  function takeKeys(input) {
    if (!keys) { keys = true; keysAt = performance.now(); send({ t: 'focus', on: true }); }
    if (input) { setTimeout(() => input.focus({ preventScroll: true }), 60); setTimeout(() => input.focus({ preventScroll: true }), 400); }
  }
  function releaseKeys(restore) {
    if (!keys) return;
    keys = false;
    send({ t: 'focus', on: false, restore: restore !== false });
  }

  /* ───── open / close ───── */
  function open(kind, trigger) {
    if (current === kind) { close(); return; }
    if (current) { close().then(() => open(kind, trigger)); return; }
    current = kind; srcEl = trigger; trigger.classList.add('src');
    geo = geometry(kind, trigger);
    for (const el of [cellEl, gCell])
      Object.assign(el.style, { left: geo.x + 'px', top: geo.y + 'px', width: geo.w + 'px', height: geo.h + 'px', transformOrigin: `${geo.ox}px ${geo.oy}px` });
    // a fresh, empty container for every cell: the listeners a cell puts on it leave with it.
    // (They used to pile up, so after a few openings one click on "next" skipped several tracks.)
    const fresh = cc.cloneNode(false); cc.replaceWith(fresh); cc = fresh;
    getComputedStyle(fresh).opacity;   // start it from opacity 0, so the content still fades in
    handler = (KINDS[kind])(cc, geo) || {};
    visible = true;
    Goo.need('cell', true);
    play(geo, spring(0, 1, 210, 19));
    recoil(geo.side, .013, .1);
    cellEl.classList.add('open');
    if (WATCH[kind]) send({ t: 'watch', what: WATCH[kind], on: true });
    postRect();
  }
  function close(restoreFocus = true) {
    if (!current) return Promise.resolve();
    const kind = current, g = geo;
    current = null;
    cellEl.classList.remove('open');
    if (srcEl) srcEl.classList.remove('src');
    srcEl = null;
    if (handler.cleanup) handler.cleanup();
    handler = {};
    if (WATCH[kind]) send({ t: 'watch', what: WATCH[kind], on: false });
    releaseKeys(restoreFocus);
    recoil(g.side, .008, .16);
    return play(g, spring(1, 0, 300, 31)).then(() => {
      if (current) return;
      cc.innerHTML = ''; visible = false;
      Goo.need('cell', false);
      postRect();
    });
  }
  // after launching something from a cell: let the new app take the foreground, fold the island
  function finish() {
    close(false);
    setTimeout(() => { S.hover = false; update(); }, 200);
  }

  function rect() {
    if (!visible || !geo) return null;
    const I = ISL(), g = geo;
    if (g.side === 'below') return { x: Math.floor(g.x - 6), y: I.y + I.h - 4, w: Math.ceil(g.w + 12), h: Math.ceil(g.y + g.h - (I.y + I.h) + 12) };
    if (g.side === 'left') return { x: Math.floor(g.x - 6), y: g.y - 4, w: Math.ceil(I.x - g.x + 10), h: Math.ceil(g.h + 12) };
    return { x: Math.floor(I.x + I.w - 4), y: g.y - 4, w: Math.ceil(g.x + g.w - (I.x + I.w) + 10), h: Math.ceil(g.h + 12) };
  }

  /* ───── shared bits ───── */
  const hl = (s, q) => {
    s = String(s ?? ''); if (!q) return esc(s);
    const i = s.toLowerCase().indexOf(q); if (i < 0) return esc(s);
    return esc(s.slice(0, i)) + '<mark>' + esc(s.slice(i, i + q.length)) + '</mark>' + esc(s.slice(i + q.length));
  };
  const fileIcon = f => f.icon ? `<span class="ftype"><img src="${f.icon}" alt=""></span>` : `<span class="ftype">${ic('doc')}</span>`;
  const deviceGlyph = name => /науш|head|airpods|buds/i.test(name) ? 'head' : /монитор|hdmi|display|displayport|lg|samsung/i.test(name) ? 'monitor' : 'speaker';

  /* ───── the cells ───── */
  const KINDS = {

    search(root) {
      root.innerHTML = `
        <label class="field s-field">${ic('search')}<input id="s-in" type="text" placeholder="Приложения, настройки, файлы" autocomplete="off" spellcheck="false"><kbd>Esc</kbd></label>
        <div class="s-res scroll" id="s-res"></div>
        <div class="s-foot"><span>↑ ↓ выбрать</span><span>Enter открыть</span><span>Esc закрыть</span></div>`;
      const inp = $('#s-in', root), res = $('#s-res', root);
      let seq = 0, data = { apps: [], settings: [], files: [], empty: true }, items = [], sel = 0, timer = 0, q = '';
      const query = () => { q = inp.value.trim().toLowerCase(); send({ t: 'search', q, seq: ++seq }); };
      function draw() {
        items = [];
        let k = 0;
        const row = (it, inner) => { items.push(it); const s = `<div class="row${k === sel ? ' sel' : ''}" data-i="${k}">${inner}</div>`; k++; return s; };
        if (data.empty) {
          const tiles = data.apps.map(a => { items.push({ kind: 'app', id: a.id }); return `<div class="s-tile" data-i="${k++}">${appIcon(a.icon, a.name, 40)}<span>${esc(a.name)}</span></div>`; }).join('');
          const files = data.files.map(f => row({ kind: 'file', id: f.id }, `${fileIcon(f)}<span class="t"><b>${esc(f.name)}</b><span>${esc(f.sub)}</span></span>`)).join('');
          res.innerHTML = (tiles ? `<div class="sec"><span class="cap">Избранное</span><div class="s-recent">${tiles}</div></div>` : '') +
            (files ? `<div class="sec"><span class="cap">Недавние файлы</span><div class="files3">${files}</div></div>` : '');
          return;
        }
        const secs = [];
        if (data.apps.length) secs.push(`<div class="sec"><span class="cap">Приложения</span>${data.apps.map(a => row({ kind: 'app', id: a.id }, `${appIcon(a.icon, a.name, 34)}<span class="t"><b>${hl(a.name, q)}</b><span>Приложение</span></span>`)).join('')}</div>`);
        if (data.settings && data.settings.length) secs.push(`<div class="sec"><span class="cap">Настройки</span>${data.settings.map(s => row({ kind: 'setting', id: s.id }, `<span class="ftype">${ic(s.glyph)}</span><span class="t"><b>${hl(s.name, q)}</b><span>Параметры Windows</span></span>`)).join('')}</div>`);
        if (data.files.length) secs.push(`<div class="sec"><span class="cap">Файлы</span>${data.files.map(f => row({ kind: 'file', id: f.id }, `${fileIcon(f)}<span class="t"><b>${hl(f.name, q)}</b><span>${esc(f.sub)}</span></span>`)).join('')}</div>`);
        else if (data.pending) secs.push(`<div class="sec"><span class="cap">Файлы</span><div class="empty" style="padding:8px 10px;text-align:left">Ищу…</div></div>`);
        res.innerHTML = secs.length ? `<div class="s-cols"><div>${secs[0]}</div><div>${secs.slice(1).join('')}</div></div>`
          : `<div class="empty">Ничего не нашлось по запросу «${esc(inp.value)}»</div>`;
        sel = Math.min(sel, Math.max(0, items.length - 1));
      }
      function go(it) {
        if (!it) return;
        if (it.kind === 'app') send({ t: 'launch', id: it.id }); else send({ t: 'open', id: it.id });
        finish();
      }
      inp.addEventListener('input', () => { sel = 0; clearTimeout(timer); timer = setTimeout(query, 110); });
      inp.addEventListener('keydown', e => {
        if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
          e.preventDefault(); if (!items.length || data.empty) return;
          sel = (sel + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length; draw();
          res.querySelector('.row.sel')?.scrollIntoView({ block: 'nearest' });
        } else if (e.key === 'Enter') { e.preventDefault(); go(items[data.empty ? 0 : sel]); }
      });
      res.addEventListener('click', e => { const r = e.target.closest('[data-i]'); if (r) go(items[+r.dataset.i]); });
      res.addEventListener('mousemove', e => { const r = e.target.closest('.row[data-i]'); if (r && !data.empty && +r.dataset.i !== sel) { sel = +r.dataset.i; draw(); } });
      query();
      takeKeys(inp);
      return {
        onHost(m) {
          if (m.seq !== seq) return;
          if (m.t === 'search.res') { data = { apps: m.apps || [], settings: m.settings || [], files: m.files || [], empty: !!m.empty, pending: !!m.pending }; draw(); }
          if (m.t === 'search.files') { data.files = m.files || []; data.pending = false; draw(); }
        },
        cleanup() { clearTimeout(timer); }
      };
    },

    cal(root, g) {
      root.innerHTML = `
        <div class="calw${g.wide ? ' wide' : ''}">
          <div>
            <div class="cal-h"><h4 id="cal-t"></h4><button class="ib" id="cal-p" title="Предыдущий месяц">${ic('left')}</button><button class="ib" id="cal-n" title="Следующий месяц">${ic('right')}</button></div>
            <div class="cal-g" id="cal-g"></div>
          </div>
          <div class="side">
            <div class="wx" id="wx"><div class="wx-msg">Загружаю погоду…</div></div>
            <div class="tm">
              <div class="tm-tabs"><button class="on" data-tm="timer">Таймер</button><button data-tm="sw">Секундомер</button></div>
              <div class="tm-big" id="tm-big"></div>
              <div class="tm-pre" id="tm-pre"><button data-m="1">1 мин</button><button data-m="5">5 мин</button><button data-m="10">10 мин</button><button data-m="25">25 мин</button></div>
              <div class="tm-laps" id="tm-laps" hidden></div>
              <div class="tm-ctl"><button id="tm-reset" title="Сбросить">${ic('reset')}</button><button class="go" id="tm-go" title="Старт / пауза">${ic('play')}</button><button id="tm-lap" title="Круг" hidden>${ic('flag')}</button></div>
            </div>
          </div>
        </div>`;
      // calendar
      const today = new Date(); let view = new Date(today.getFullYear(), today.getMonth(), 1);
      function drawCal() {
        $('#cal-t', root).textContent = view.toLocaleDateString('ru-RU', { month: 'long', year: 'numeric' });
        const first = (view.getDay() + 6) % 7, days = new Date(view.getFullYear(), view.getMonth() + 1, 0).getDate();
        const prevDays = new Date(view.getFullYear(), view.getMonth(), 0).getDate();
        let h = ['пн', 'вт', 'ср', 'чт', 'пт', 'сб', 'вс'].map(d => `<div class="dw">${d}</div>`).join('');
        for (let i = 0; i < 42; i++) {
          const d = i - first + 1, out = d < 1 || d > days, num = out ? (d < 1 ? prevDays + d : d - days) : d;
          const isToday = !out && view.getFullYear() === today.getFullYear() && view.getMonth() === today.getMonth() && d === today.getDate();
          h += `<div class="d${out ? ' out' : ''}${i % 7 > 4 && !out ? ' we' : ''}${isToday ? ' today' : ''}">${num}</div>`;
        }
        $('#cal-g', root).innerHTML = h;
      }
      $('#cal-p', root).onclick = () => { view = new Date(view.getFullYear(), view.getMonth() - 1, 1); drawCal(); };
      $('#cal-n', root).onclick = () => { view = new Date(view.getFullYear(), view.getMonth() + 1, 1); drawCal(); };
      drawCal();
      // weather
      const WMO = c => c === 0 ? ['sun', 'ясно'] : c <= 2 ? ['pcloud', 'переменная облачность'] : c === 3 ? ['cloud', 'пасмурно'] : c <= 48 ? ['fog', 'туман']
        : c <= 57 ? ['rain', 'морось'] : c <= 67 ? ['rain', 'дождь'] : c <= 77 ? ['snow', 'снег'] : c <= 82 ? ['rain', 'ливень'] : c <= 86 ? ['snow', 'снегопад'] : ['storm', 'гроза'];
      const wx = $('#wx', root);
      function askCity(msg) {
        wx.innerHTML = `<div class="wx-msg">${esc(msg)}</div><label class="field">${ic('search')}<input id="wx-city" type="text" placeholder="Город, например Москва" autocomplete="off" spellcheck="false" value="${esc(S.config.city || '')}"></label>`;
        const inp = $('#wx-city', wx);
        inp.addEventListener('focus', () => takeKeys());
        inp.addEventListener('keydown', e => {
          if (e.key === 'Enter' && inp.value.trim()) { S.config.city = inp.value.trim(); send({ t: 'weather.city', city: S.config.city }); wx.innerHTML = '<div class="wx-msg">Загружаю погоду…</div>'; releaseKeys(true); }
        });
      }
      function drawWeather(m) {
        if (m.need) { askCity('Укажите город, чтобы видеть погоду'); return; }
        if (m.error) { askCity(m.error + (m.city ? ` (${m.city})` : '')); return; }
        const [glyph, desc] = WMO(m.code);
        wx.innerHTML = `<div class="wx-now">${ic(glyph)}<span class="big">${m.temp}°</span><div><b id="wx-c" title="Сменить город">${esc(m.city)}</b>${desc} · ощущается ${m.feels}°</div></div>
          <div class="wx-h">${m.hours.map(h => `<div>${h.h}${ic(WMO(h.code)[0])}<b>${h.temp}°</b></div>`).join('')}</div>`;
        $('#wx-c', wx).onclick = () => askCity('Другой город:');
      }
      // timer + stopwatch (state lives in app.js so it keeps running with the cell closed)
      let mode = S.sw.running ? 'sw' : 'timer';
      const big = $('#tm-big', root), go = $('#tm-go', root);
      const f2 = n => String(n).padStart(2, '0');
      function drawTimer() {
        const sw = mode === 'sw';
        $$('.tm-tabs button', root).forEach(b => b.classList.toggle('on', b.dataset.tm === mode));
        big.classList.toggle('sw-mode', sw); go.classList.toggle('sw-mode', sw);
        $('#tm-pre', root).hidden = sw; $('#tm-laps', root).hidden = !sw; $('#tm-lap', root).hidden = !sw;
        if (!sw) { const s = Math.ceil(timerLeft()); big.textContent = `${f2(Math.floor(s / 60))}:${f2(s % 60)}`; go.innerHTML = ic(S.timer.running ? 'pause' : 'play'); }
        else {
          const cs = Math.floor(swMs() / 10); big.textContent = `${f2(Math.floor(cs / 6000))}:${f2(Math.floor(cs / 100) % 60)},${f2(cs % 100)}`;
          go.innerHTML = ic(S.sw.running ? 'pause' : 'play');
          $('#tm-laps', root).textContent = S.sw.laps.slice(0, 2).map((l, i) => `Круг ${S.sw.laps.length - i}: ${fmtTime(l / 1000)}`).join(' · ');
        }
      }
      go.onclick = () => {
        if (mode === 'timer') {
          const t = S.timer;
          if (t.running) { t.left = timerLeft(); t.running = false; }
          else { if (t.left <= 0) t.left = t.total; t.endAt = Date.now() + t.left * 1000; t.running = true; }
        } else {
          const s = S.sw;
          if (s.running) { s.ms = swMs(); s.running = false; } else { s.startAt = Date.now(); s.running = true; }
        }
        ensureTicking(); drawTimer();
      };
      $('#tm-reset', root).onclick = () => {
        if (mode === 'timer') { S.timer.running = false; S.timer.left = S.timer.total; }
        else { S.sw.running = false; S.sw.ms = 0; S.sw.laps = []; }
        ensureTicking(); drawTimer();
      };
      $('#tm-lap', root).onclick = () => { if (S.sw.running) { S.sw.laps.unshift(swMs()); drawTimer(); } };
      $('#tm-pre', root).onclick = e => { const m = +e.target.dataset?.m; if (m) { S.timer.total = S.timer.left = m * 60; S.timer.running = false; ensureTicking(); drawTimer(); } };
      $$('.tm-tabs button', root).forEach(b => b.onclick = () => { mode = b.dataset.tm; drawTimer(); });
      drawTimer();
      const live = setInterval(() => { if (mode === 'sw' && S.sw.running) drawTimer(); }, 60);
      return {
        onHost(m) { if (m.t === 'weather') drawWeather(m); },
        onTick: drawTimer,
        cleanup() { clearInterval(live); }
      };
    },

    player(root) {
      root.innerHTML = `
        <div class="empty" id="bp-none" style="padding-top:70px" hidden>Сейчас ничего не играет</div>
        <div class="bp" id="bp">
          <div class="cover" id="bp-cover"></div>
          <div style="min-width:0">
            <div class="bp-t"><b id="bp-title"></b><span id="bp-art"></span><small id="bp-app"></small></div>
            <div class="bp-prog" id="bp-prog"><span id="bp-cur">0:00</span><input class="rng" type="range" id="bp-pos" min="0" max="100" step="1" aria-label="Позиция трека"><span id="bp-dur">0:00</span></div>
            <div class="bp-ctl">
              <button class="ib" data-pa="prev" id="bp-prev" title="Предыдущий">${ic('prev')}</button>
              <button class="ib pp" data-pa="play" title="Пауза / воспроизведение">${ic('pause', 'i-pause')}${ic('play', 'i-play')}</button>
              <button class="ib" data-pa="next" id="bp-next" title="Следующий">${ic('next')}</button>
            </div>
          </div>
        </div>`;
      const pos = $('#bp-pos', root);
      let seeking = false;
      function draw() {
        const m = S.media;
        $('#bp-none', root).hidden = !!m.has; $('#bp', root).hidden = !m.has;
        if (!m.has) return;
        setCover($('#bp-cover', root), m);
        $('#bp-title', root).textContent = m.title || '';
        $('#bp-art', root).textContent = m.artist || '';
        if (m.app) $('#bp-app', root).textContent = m.app;   // the app name can be missing for a moment while it switches tracks
        $('#bp-prev', root).disabled = !m.canPrev; $('#bp-next', root).disabled = !m.canNext;
        $('#bp-prog', root).style.visibility = m.dur > 0 ? '' : 'hidden';
        pos.disabled = !m.canSeek;
        tick();
      }
      function tick() {
        const m = S.media; if (!m.dur || seeking || !$('#bp-pos', root)) return;
        const p = mediaPos();
        pos.max = Math.round(m.dur); pos.value = Math.round(p);
        paintRange(pos, (p / m.dur * 100).toFixed(2));
        $('#bp-cur', root).textContent = fmtTime(p); $('#bp-dur', root).textContent = fmtTime(m.dur);
      }
      pos.addEventListener('input', () => { seeking = true; paintRange(pos, pos.value / pos.max * 100); $('#bp-cur', root).textContent = fmtTime(+pos.value); });
      pos.addEventListener('change', () => { send({ t: 'cmd', c: 'seek', v: +pos.value }); S.media.pos = +pos.value; S.media.at = performance.now(); seeking = false; });
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-pa]'); if (!b) return;
        if (b.dataset.pa === 'play') document.body.dataset.playing = document.body.dataset.playing === '1' ? '0' : '1';
        send({ t: 'cmd', c: b.dataset.pa });
      });
      draw();
      const t = setInterval(tick, 500);
      return { onMedia: draw, cleanup() { clearInterval(t); } };
    },

    favs(root, g) {
      root.innerHTML = `
        <div class="favw${g.wide ? ' wide' : ''}">
          <div class="c-head" style="margin-bottom:0"><h4>Избранное</h4><span class="grow"></span><span class="c-sub" id="fv-hint">тяните за точки</span></div>
          <div class="scroll" id="fv-scroll"><div class="fv-list" id="fv-list"></div></div>
          <div id="fv-foot"><button class="pill-btn" id="fv-add" style="width:100%">${ic('plus')}Добавить приложение</button></div>
        </div>`;
      let list = S.favs.map(f => ({ ...f })), dragging = null, adding = false, seq = 0;
      const box = $('#fv-list', root);
      const save = () => send({ t: 'favs.save', list: list.map(f => f.id) });
      function draw() {
        if (adding) return;
        box.innerHTML = list.length ? list.map((f, i) => `<div class="fv" data-i="${i}"><span class="grip">${ic('grip')}</span>${appIcon(f.icon, f.name, 28)}<b>${esc(f.name)}</b><button class="rm" title="Убрать из избранного">${ic('minus')}</button></div>`).join('')
          : '<div class="empty">Пока пусто. Добавьте приложения кнопкой ниже.</div>';
      }
      box.addEventListener('click', e => {
        const rm = e.target.closest('.rm'); if (rm) { list.splice(+rm.closest('.fv').dataset.i, 1); draw(); save(); return; }
        const res = e.target.closest('[data-add]');
        if (res) { const it = JSON.parse(res.dataset.add); if (!list.some(f => f.id === it.id)) list.push(it); stopAdding(); save(); }
      });
      box.addEventListener('pointerdown', e => {
        const gr = e.target.closest('.grip'); if (!gr) return;
        const row = gr.closest('.fv'); dragging = { row, startY: e.clientY, i: +row.dataset.i };
        row.classList.add('dragging'); row.setPointerCapture(e.pointerId); e.preventDefault();
      });
      box.addEventListener('pointermove', e => {
        if (!dragging) return;
        const dy = e.clientY - dragging.startY, rowH = 40;
        dragging.row.style.transform = `translateY(${dy}px)`;
        const target = clamp(dragging.i + Math.round(dy / rowH), 0, list.length - 1);
        if (target !== dragging.i) {
          const [f] = list.splice(dragging.i, 1); list.splice(target, 0, f);
          dragging.startY += (target - dragging.i) * rowH; dragging.i = target;
          const pid = e.pointerId; draw();
          dragging.row = box.children[target]; dragging.row.classList.add('dragging');
          try { dragging.row.setPointerCapture(pid); } catch (_) {}
          dragging.row.style.transform = `translateY(${e.clientY - dragging.startY}px)`;
        }
      });
      const endDrag = () => { if (!dragging) return; dragging.row.classList.remove('dragging'); dragging.row.style.transform = ''; dragging = null; save(); };
      box.addEventListener('pointerup', endDrag); box.addEventListener('pointercancel', endDrag);
      function startAdding() {
        adding = true;
        $('#fv-hint', root).textContent = 'найдите приложение';
        $('#fv-foot', root).innerHTML = `<label class="field">${ic('search')}<input id="fv-q" type="text" placeholder="Название приложения" autocomplete="off" spellcheck="false"></label>`;
        box.innerHTML = '<div class="empty">Начните вводить название</div>';
        const inp = $('#fv-q', root);
        let tm = 0;
        inp.addEventListener('input', () => { clearTimeout(tm); tm = setTimeout(() => send({ t: 'search', q: inp.value, seq: ++seq, appsOnly: true }), 120); });
        inp.addEventListener('keydown', e => { if (e.key === 'Escape') { e.stopPropagation(); stopAdding(); } });
        takeKeys(inp);
      }
      function stopAdding() {
        adding = false; releaseKeys(false);
        $('#fv-hint', root).textContent = 'тяните за точки';
        $('#fv-foot', root).innerHTML = `<button class="pill-btn" id="fv-add" style="width:100%">${ic('plus')}Добавить приложение</button>`;
        $('#fv-add', root).onclick = startAdding;
        draw();
      }
      $('#fv-add', root).onclick = startAdding;
      draw();
      return {
        onHost(m) {
          if (m.t !== 'search.res' || m.seq !== seq || !adding) return;
          box.innerHTML = (m.apps || []).length ? m.apps.map(a => `<div class="row" data-add='${esc(JSON.stringify({ id: a.id, name: a.name, icon: a.icon }))}'>${appIcon(a.icon, a.name, 28)}<span class="t"><b>${esc(a.name)}</b></span>${ic('plus')}</div>`).join('')
            : '<div class="empty">Ничего не нашлось</div>';
        },
        onFavs() { if (!dragging && !adding) { list = S.favs.map(f => ({ ...f })); draw(); } }
      };
    },

    bt(root) {
      root.innerHTML = `
        <div class="c-head"><h4>Bluetooth</h4><button class="sw" id="bt-sw" title="Bluetooth"></button><span class="grow"></span><button class="pill-btn" id="bt-add">${ic('plus')}Добавить устройство</button></div>
        <div class="grid2 scroll" id="bt-list" style="max-height:${236 - 32 - 42}px"><div class="empty" style="grid-column:1/-1">Ищу устройства…</div></div>`;
      const list = $('#bt-list', root), sw = $('#bt-sw', root);
      const pending = new Map();
      let devices = [];
      const syncSwitch = () => sw.classList.toggle('on', !!(S.radios && S.radios.bt));
      sw.onclick = () => { sw.classList.toggle('on'); send({ t: 'cmd', c: 'bt' }); };
      $('#bt-add', root).onclick = () => { send({ t: 'cmd', c: 'btsettings' }); finish(); };
      function draw() {
        if (S.radios && S.radios.bt === false) { list.innerHTML = '<div class="empty" style="grid-column:1/-1">Bluetooth выключен</div>'; return; }
        if (!devices.length) { list.innerHTML = '<div class="empty" style="grid-column:1/-1">Нет сопряжённых устройств</div>'; return; }
        const now = Date.now();
        list.innerHTML = devices.map(d => {
          const busy = pending.get(d.id) > now;
          const state = busy ? (d.connected ? 'Отключаю…' : 'Подключаю…') : d.connected ? 'Подключено' : 'Не подключено';
          return `<div class="row" data-id="${esc(d.id)}"><span class="dot${d.connected ? ' on' : ''}${busy ? ' busy' : ''}">${ic(d.kind)}</span><span class="t"><b>${esc(d.name)}</b><span>${state}</span></span></div>`;
        }).join('');
      }
      list.addEventListener('click', e => {
        const r = e.target.closest('[data-id]'); if (!r) return;
        const d = devices.find(x => x.id === r.dataset.id); if (!d) return;
        if (d.canToggle) { pending.set(d.id, Date.now() + 6000); send({ t: 'bt.device', id: d.id, connect: !d.connected }); draw(); }
        else { send({ t: 'cmd', c: 'btsettings' }); finish(); }
      });
      syncSwitch();
      return {
        onHost(m) { if (m.t === 'bt') { devices = m.devices || []; draw(); } },
        onRadios() { syncSwitch(); draw(); }
      };
    },

    wifi(root) {
      root.innerHTML = `
        <div class="c-head"><h4>Wi-Fi</h4><button class="sw" id="wf-sw" title="Wi-Fi"></button><span class="grow"></span><button class="pill-btn" id="wf-set">${ic('gear')}Параметры сети</button></div>
        <div class="grid2 scroll" id="wf-list" style="max-height:${300 - 32 - 42}px"><div class="empty" style="grid-column:1/-1">Ищу сети…</div></div>`;
      const list = $('#wf-list', root), sw = $('#wf-sw', root);
      let nets = [], open = null, error = null, failed = null;
      const syncSwitch = () => sw.classList.toggle('on', !!(S.radios && S.radios.wifi));
      sw.onclick = () => { sw.classList.toggle('on'); send({ t: 'cmd', c: 'wifi' }); };
      $('#wf-set', root).onclick = () => { send({ t: 'cmd', c: 'wifisettings' }); finish(); };
      const bars = s => s >= 66 ? 'wifi' : s >= 33 ? 'wifi2' : 'wifi1';
      function draw() {
        if (error === 'none') { list.innerHTML = '<div class="empty" style="grid-column:1/-1">Wi-Fi-адаптер не найден</div>'; return; }
        if (error === 'location') {
          list.innerHTML = `<div class="empty" style="grid-column:1/-1">Windows показывает названия сетей только программам с доступом к местоположению.<br><br><button class="pill-btn" id="wf-loc">Открыть настройки</button></div>`;
          $('#wf-loc', list).onclick = () => { send({ t: 'cmd', c: 'location' }); finish(); };
          return;
        }
        if (S.radios && S.radios.wifi === false) { list.innerHTML = '<div class="empty" style="grid-column:1/-1">Wi-Fi выключен</div>'; return; }
        if (!nets.length) { list.innerHTML = '<div class="empty" style="grid-column:1/-1">Сети не найдены</div>'; return; }
        list.innerHTML = nets.map(n => {
          const sub = n.connected ? 'Подключено' : n.saved ? 'Сохранённая сеть' : n.secure ? 'Защищённая' : 'Открытая';
          let html = `<div class="row net${n.connected ? ' sel' : ''}" data-ssid="${esc(n.ssid)}"><span class="dot${n.connected ? ' on' : ''}">${ic(bars(n.signal))}</span><span class="t"><b>${esc(n.ssid)}</b><span>${sub}</span></span>${n.secure ? ic('lock', 'lockic') : ''}</div>`;
          if (open === n.ssid) {
            if (n.connected) html += `<div class="net-pass"><button class="pill-btn" data-disc>Отключиться</button></div>`;
            else html += `<div class="net-pass"><label class="field">${ic('lock')}<input id="wf-pass" type="password" placeholder="Пароль сети" autocomplete="off"></label><button class="pill-btn primary" data-conn>Подключить</button></div>` +
              (failed === n.ssid ? '<div class="err" style="grid-column:1/-1">Не удалось подключиться. Проверьте пароль.</div>' : '');
          }
          return html;
        }).join('');
        const pass = $('#wf-pass', list);
        if (pass) {
          takeKeys(pass);
          pass.addEventListener('keydown', e => { if (e.key === 'Enter') connect(open, pass.value); });
        }
      }
      function connect(ssid, password) { failed = null; send({ t: 'wifi.connect', ssid, password }); open = null; releaseKeys(true); draw(); }
      list.addEventListener('click', e => {
        if (e.target.closest('[data-disc]')) { send({ t: 'wifi.disconnect' }); open = null; draw(); return; }
        if (e.target.closest('[data-conn]')) { connect(open, ($('#wf-pass', list) || {}).value || ''); return; }
        const r = e.target.closest('[data-ssid]'); if (!r) return;
        const n = nets.find(x => x.ssid === r.dataset.ssid); if (!n) return;
        if (!n.connected && (n.saved || !n.secure)) { connect(n.ssid, null); return; }
        open = open === n.ssid ? null : n.ssid;
        if (!open) releaseKeys(true);
        draw();
      });
      syncSwitch();
      return {
        onHost(m) {
          if (m.t === 'wifi') { nets = m.networks || []; error = m.error; if (!open) draw(); }
          if (m.t === 'wifi.result' && !m.ok) { failed = m.ssid; open = m.ssid; draw(); }
        },
        onRadios() { syncSwitch(); if (!open) draw(); }
      };
    },

    sound(root) {
      root.innerHTML = `
        <div class="snd">
          <div class="scroll">
            <div class="sec"><span class="cap">Вывод звука</span><div id="snd-out"></div></div>
            <div class="sec"><span class="cap">Микрофон</span><div id="snd-mic"></div><div id="snd-in"></div></div>
          </div>
          <div class="scroll"><div class="sec"><span class="cap">Приложения</span><div id="snd-apps"><div class="empty">Загружаю…</div></div></div></div>
        </div>`;
      let shape = '', active = null;
      const out = $('#snd-out', root), inn = $('#snd-in', root), mic = $('#snd-mic', root), apps = $('#snd-apps', root);
      const devRows = (list, kind) => list.map(d => `<div class="row${d.def ? ' sel' : ''}" data-dev="${esc(d.id)}" data-kind="${kind}"><span class="dot">${ic(kind === 'in' ? 'mic' : deviceGlyph(d.name + ' ' + d.sub))}</span><span class="t"><b>${esc(d.name)}</b><span>${esc(d.sub)}</span></span>${d.def ? ic('check', 'check') : ''}</div>`).join('');
      function draw(m) {
        const key = JSON.stringify([m.outputs.map(d => d.id + d.def), m.inputs.map(d => d.id + d.def), m.apps.map(a => a.id + a.mute), m.mic.has, m.mic.mute]);
        if (key !== shape) {
          shape = key;
          out.innerHTML = devRows(m.outputs, 'out') || '<div class="empty">Нет устройств вывода</div>';
          inn.innerHTML = m.inputs.length > 1 ? devRows(m.inputs.filter(d => !d.def), 'in') : '';
          const def = m.inputs.find(d => d.def);
          mic.innerHTML = m.mic.has
            ? `<div class="mic"><button class="dot${m.mic.mute ? ' off' : ''}" data-micmute title="Выключить микрофон">${ic(m.mic.mute ? 'micoff' : 'mic')}</button><div style="min-width:0"><b style="font-weight:500;font-size:12.5px;display:block;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;margin-bottom:6px">${esc(def ? def.name + (def.sub ? ' · ' + def.sub : '') : 'Микрофон')}</b><input class="rng" type="range" id="mic-lv" min="0" max="100" aria-label="Громкость микрофона"></div><em id="mic-n"></em></div>`
            : '<div class="empty">Микрофон не найден</div>';
          apps.innerHTML = m.apps.length ? m.apps.map((a, i) => `<div class="mix${a.mute ? ' muted' : ''}" data-app="${esc(a.id)}">${a.id === 'sys' ? `<span class="dot" style="width:28px;height:28px" data-appmute>${ic('bell')}</span>` : `<span data-appmute>${appIcon(a.icon, a.name, 28)}</span>`}<b title="${esc(a.name)}">${esc(a.name)}</b><input class="rng" type="range" id="app-${i}" min="0" max="100" aria-label="Громкость ${esc(a.name)}"><em></em></div>`).join('')
            : '<div class="empty">Сейчас ни одна программа не играет звук</div>';
        }
        m.apps.forEach((a, i) => {
          const r = $(`#app-${i}`, root); if (!r || r === active) return;
          const p = Math.round(a.v * 100); r.value = p; paintRange(r, p); r.nextElementSibling.textContent = p;
        });
        const ml = $('#mic-lv', root);
        if (ml && ml !== active) { const p = Math.round(m.mic.v * 100); ml.value = p; paintRange(ml, p); $('#mic-n', root).textContent = p; }
      }
      root.addEventListener('pointerdown', e => { if (e.target.classList.contains('rng')) active = e.target; });
      root.addEventListener('change', e => { if (e.target === active) active = null; });
      root.addEventListener('input', e => {
        const r = e.target; if (!r.classList.contains('rng')) return;
        paintRange(r, r.value);
        if (r.id === 'mic-lv') { $('#mic-n', root).textContent = r.value; send({ t: 'audio.mic', v: r.value / 100 }); return; }
        r.nextElementSibling.textContent = r.value;
        send({ t: 'audio.app', id: r.closest('[data-app]').dataset.app, v: r.value / 100 });
      });
      root.addEventListener('click', e => {
        const dev = e.target.closest('[data-dev]');
        if (dev) { send({ t: dev.dataset.kind === 'in' ? 'audio.in' : 'audio.out', id: dev.dataset.dev }); return; }
        if (e.target.closest('[data-micmute]')) { send({ t: 'audio.micMute' }); return; }
        const am = e.target.closest('[data-appmute]');
        if (am) send({ t: 'audio.appMute', id: am.closest('[data-app]').dataset.app });
      });
      return { onHost(m) { if (m.t === 'audio') draw(m); } };
    },

    cpu(root) {
      root.innerHTML = `
        <div class="cpuw">
          <div style="min-width:0"><div class="c-head" style="margin-bottom:4px"><h4>Больше всего нагружают</h4></div><div id="procs"><div class="empty">Считаю…</div></div></div>
          <div class="cpu-sum">
            <div class="cpu-big"><div class="gauge"><svg viewBox="0 0 64 64"><circle class="g-tr" cx="32" cy="32" r="26"/><circle class="g-val" id="cpu-ring" cx="32" cy="32" r="26"/></svg></div><div><b id="cpu-tot">–</b><span id="cpu-sub">загрузка процессора</span></div></div>
            <div><div class="memline"><span>Память</span><span id="mem-t"></span></div><div class="membar"><i id="mem-b"></i></div></div>
            <div id="gpu-row" hidden><div class="memline"><span>Видеокарта</span><span id="gpu-t"></span></div><div class="membar"><i id="gpu-b" style="background:linear-gradient(90deg,#8fd3ff,#3a8fe0)"></i></div></div>
            <button class="pill-btn" id="cpu-tm">${ic('task')}Диспетчер задач</button>
          </div>
        </div>`;
      $('#cpu-tm', root).onclick = () => { send({ t: 'cmd', c: 'taskmgr' }); finish(); };
      return {
        onHost(m) {
          if (m.t !== 'procs') return;
          $('#cpu-tot', root).textContent = m.load + '%';
          $('#cpu-sub', root).textContent = m.temp != null ? `загрузка · ${m.temp}°` : 'загрузка процессора';
          $('#cpu-ring', root).style.strokeDashoffset = (163.4 * (1 - m.load / 100)).toFixed(1);
          $('#mem-t', root).innerHTML = `<b>${String(m.used).replace('.', ',')}</b> из ${Math.round(m.total)} ГБ`;
          $('#mem-b', root).style.width = (m.used / m.total * 100).toFixed(1) + '%';
          $('#gpu-row', root).hidden = m.gpu == null;
          if (m.gpu != null) { $('#gpu-t', root).innerHTML = `<b>${m.gpu}%</b> загрузка`; $('#gpu-b', root).style.width = m.gpu + '%'; }
          if (m.list.length) $('#procs', root).innerHTML = m.list.map(p => `<div class="proc">${appIcon(p.icon, p.name, 28)}<div style="min-width:0"><b>${esc(p.name)}</b><div class="bar"><i style="width:${Math.min(100, p.cpu * 2)}%"></i></div></div><em>${p.cpu.toFixed(p.cpu < 10 ? 1 : 0).replace('.', ',')}%<small>${p.mem >= 1024 ? (p.mem / 1024).toFixed(1).replace('.', ',') + ' ГБ' : p.mem + ' МБ'}</small></em></div>`).join('');
        }
      };
    },

    power(root, g) {
      root.innerHTML = `
        <div class="pwr${g.wide ? ' wide' : ''}">
          <button class="pw" data-p="sleep"><i>${ic('moon')}</i>Сон</button>
          <button class="pw danger" data-p="restart"><i>${ic('restart')}</i>Перезагрузка</button>
          <button class="pw danger" data-p="shutdown"><i>${ic('power')}</i>Выключение</button>
        </div>`;
      root.addEventListener('click', e => {
        const b = e.target.closest('[data-p]'); if (!b) return;
        finish();
        setTimeout(() => send({ t: 'cmd', c: b.dataset.p }), 250);
      });
      return {};
    }
  };

  return {
    toggle: open,
    close: (restore = true) => close(restore),
    isOpen: () => !!current,
    hasKeys: () => keys,
    justTookKeys: () => performance.now() - keysAt < 800,
    holdsFocus: () => keys && document.hasFocus(),
    rect,
    onHost: m => { if (handler && handler.onHost) handler.onHost(m); },
    onTick: () => { if (handler && handler.onTick) handler.onTick(); },
    onMedia: () => { if (handler && handler.onMedia) handler.onMedia(); },
    onRadios: () => { if (handler && handler.onRadios) handler.onRadios(); },
    onFavs: () => { if (handler && handler.onFavs) handler.onFavs(); }
  };
})();

/* ───────── start ───────── */
clock();
renderSys();
update(true);            // starts hidden above the screen edge (S.intro)
send({ t: 'ready' });
setTimeout(introStart, 2500);   // in case the settings never arrive
