<%@ Page Title="Loaded tariffs" Language="C#" MasterPageFile="~/Site.Master" AutoEventWireup="true" CodeBehind="Loaded.aspx.cs" Inherits="TariffHub.Loaded" %>

<asp:Content ID="Main" ContentPlaceHolderID="MainContent" runat="server">
<%-- What is loaded: a world map of the country pairs every carrier prices (Ground / Air) and one row per carrier.
     Read live from the rate tables (cached 10 minutes; "Refresh now" reads again). Scripts come from the app itself (no CDN). --%>
<style>
.cov { --pc:#0e6b5c; --city:#58a693; --zone:#b9d8cf; --arc:#c2562d; --sel:#1d3557; --land:#e3e6ea; --sea:#eef3f8;
       --a1:#e3ecf7; --a2:#9dbbe0; --a3:#4f86c6; --a4:#1d4f8f; --line:#d5dae1; --mono:Consolas,"Courier New",monospace;
       display:grid; gap:10px; }
.cov h2 { margin:0; font-size:18px; color:#1d3557; }
.cov .head { display:flex; flex-wrap:wrap; gap:6px 16px; align-items:baseline; justify-content:space-between; }
.cov .head p { margin:2px 0 0; max-width:95ch; }
.cov .totals { display:flex; flex-wrap:wrap; gap:6px 22px; background:#fff; border:1px solid var(--line); border-radius:6px; padding:8px 12px; }
.cov .stat b { display:block; font:600 18px var(--mono); color:#1d3557; font-variant-numeric:tabular-nums; }
.cov .stat span { color:#6b7280; font-size:12px; }
.cov .bar { display:flex; flex-wrap:wrap; gap:10px; align-items:center; justify-content:space-between; }
.cov .seg { display:inline-flex; gap:2px; background:#fff; border:1px solid var(--line); border-radius:5px; padding:2px; }
.cov .seg button { border:0; background:transparent; color:#4b5563; font-weight:600; padding:4px 14px; }
.cov .seg button[aria-pressed="true"] { background:#1d3557; color:#fff; }
.cov .stats { display:flex; flex-wrap:wrap; gap:16px; }
.cov .main { display:grid; grid-template-columns:minmax(0,1fr) 400px; gap:10px; }
@media (max-width:900px) { .cov .main { grid-template-columns:minmax(0,1fr); } }
.cov .mapbox { position:relative; background:var(--sea); border:1px solid var(--line); border-radius:6px; overflow:hidden; min-height:300px; }
.cov svg { display:block; width:100%; height:auto; touch-action:none; }
.cov .country { fill:var(--land); stroke:#fff; stroke-width:.4; cursor:pointer; }
.cov .country:hover { stroke:#1c2430; stroke-width:.9; }
.cov .country.sel { stroke:var(--sel); stroke-width:1.6; }
.cov .arc { fill:none; stroke-linecap:round; pointer-events:none; opacity:.85; }
.cov .legend { position:absolute; left:10px; bottom:10px; background:#fff; border:1px solid var(--line); border-radius:5px; padding:6px 9px; font-size:12px; display:grid; gap:3px; }
.cov .legend i { display:inline-block; width:12px; height:12px; border-radius:2px; margin-right:6px; vertical-align:-2px; }
.cov .tip { position:absolute; pointer-events:none; background:#1c2430; color:#fff; padding:5px 8px; border-radius:4px; font-size:12px; max-width:280px; }
.cov .hint { position:absolute; right:10px; top:8px; color:#6b7280; font-size:11px; }
.cov .panel { background:#fff; border:1px solid var(--line); border-radius:6px; padding:10px 12px; display:grid; gap:8px; align-content:start; max-height:620px; overflow:auto; }
.cov .panel h3 { margin:0; }
.cov .panel table th { position:static; }
.cov td.n, .cov th.n { text-align:right; white-space:nowrap; }
.cov td.n { font-family:var(--mono); font-variant-numeric:tabular-nums; }
.cov .lv { font:600 10.5px var(--mono); padding:1px 5px; border-radius:3px; white-space:nowrap; }
.cov .lv.POSTCODE { background:var(--pc); color:#fff; } .cov .lv.CITY { background:var(--city); color:#fff; } .cov .lv.COUNTRY { background:var(--zone); color:#1c2430; }
.cov .lv.AIRPORT { background:var(--a4); color:#fff; }
.cov .codes { font-family:var(--mono); font-size:11.5px; color:#4b5563; }
.cov .carriers { max-height:none; }
.cov .carriers tr.tot td { font-weight:600; background:#eef1f5; border-top:2px solid #c9d3e2; }
.cov .carriers tr.mode td { background:#e9eef6; font-weight:600; color:#1d3557; }
.cov .foot { max-width:120ch; }
</style>

<div class="cov">
  <div class="head">
    <div>
      <h2>Loaded tariffs</h2>
      <p class="muted">Everything in the rate tables right now: where each carrier can price a shipment, and how much is loaded per carrier.
         Click a country on the map to see every country it connects to, at what detail and by which carriers.</p>
    </div>
    <div class="small muted">Read <span id="builtAt"></span> · <a class="link" href="<%: ResolveUrl("~/Loaded.aspx") %>?refresh=1">Refresh now</a></div>
  </div>
  <asp:Literal ID="litError" runat="server" />

  <div class="totals" id="totals"></div>

  <div class="bar">
    <div class="seg" role="group" aria-label="Mode">
      <button type="button" id="tGround" aria-pressed="true">Ground</button><button type="button" id="tAir" aria-pressed="false">Air</button>
    </div>
    <div class="stats" id="stats"></div>
  </div>
  <div class="main">
    <div class="mapbox" id="mapbox">
      <svg id="map" role="img" aria-label="World map of rate coverage"></svg>
      <div class="legend" id="legend"></div>
      <div class="hint">scroll / pinch to zoom · drag to pan</div>
      <div class="tip" id="tip" hidden></div>
    </div>
    <aside class="panel" id="panel"></aside>
  </div>

  <h3>Loaded per carrier</h3>
  <div class="scroll"><table class="carriers" id="carriers"></table></div>
  <p class="foot small muted" id="foot"></p>
</div>

<script src="<%: Asset("d3.min.js") %>"></script>
<script src="<%: Asset("topojson-client.min.js") %>"></script>
<script src="<%: Asset("world.js") %>"></script>
<script>
const DATA = <%= DataJson %>;
(function () {
if (!DATA || !window.d3 || !window.topojson || !window.TH_WORLD) { return; }
const fmt = v => (+v).toLocaleString('en');
const name = c => DATA.names[c] || c;
const RANK = {COUNTRY:1, CITY:2, POSTCODE:3, AIRPORT:4};
const GLVL = {POSTCODE:'postcode → postcode', CITY:'city → city', COUNTRY:'zone / country'};
const ALVL = ['', 'zone / country', 'city → city', 'postcode zone', 'airport → airport'];
const esc = s => String(s == null ? '' : s).replace(/[&<>"]/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;'}[ch]));
document.getElementById('builtAt').textContent = DATA.built;

// ---------- pairs per mode: [carrier, from, to, level, postcode / airport pairs, freight rows]
function pairsOf(lanes) {
  const m = new Map();
  for (const [car, o, d, lv, n] of lanes) {
    const k = o + '>' + d; let p = m.get(k);
    if (!p) { p = {o, d, best: 0, lvl: 'COUNTRY', carriers: new Map(), n: 0}; m.set(k, p); }
    const prev = p.carriers.get(car);
    if (!prev || RANK[lv] > RANK[prev]) p.carriers.set(car, lv);
    if (RANK[lv] > p.best) { p.best = RANK[lv]; p.lvl = lv; }
    p.n += +n;
  }
  return m;
}
const g = pairsOf(DATA.ground), a = pairsOf(DATA.air);
const zipMap = new Map(DATA.zip.map(z => [z[0] + '>' + z[1], z]));
function countryStats(pairs) {
  const m = new Map();
  for (const p of pairs.values()) for (const [c, dir] of [[p.o, 'out'], [p.d, 'in']]) {
    let s = m.get(c); if (!s) { s = {out: 0, in: 0, best: 0}; m.set(c, s); }
    s[dir]++; s.best = Math.max(s.best, p.best);
  }
  return m;
}
const gStats = countryStats(g), aStats = countryStats(a);

// ---------- totals and the carrier table
(function totals() {
  const C = DATA.carriers; let rows = 0, extra = 0; const cars = new Set();
  C.forEach(r => { rows += r[2] + r[9] + r[10] + r[11]; extra += r[10]; cars.add(r[1]); });
  const st = (v, l) => `<div class="stat"><b>${fmt(v)}</b><span>${l}</span></div>`;
  document.getElementById('totals').innerHTML =
    st(cars.size, 'carriers') + st(rows, 'rows loaded (rates, zone charts, extra charges, transit)') +
    st(g.size, 'ground country pairs') + st(a.size, 'air country pairs') +
    st(new Set([...gStats.keys(), ...aStats.keys()]).size, 'countries covered');

  const pairs = {Ground: new Map(), Air: new Map()};
  for (const [mode, lanes] of [['Ground', DATA.ground], ['Air', DATA.air]])
    for (const l of lanes) pairs[mode].set(l[0], (pairs[mode].get(l[0]) || 0) + 1);
  let h = '<thead><tr><th>Carrier</th><th>Services</th><th class="n">Rate rows</th><th class="n">of which freight</th><th class="n">Zone chart rows</th>' +
          '<th class="n">Extra charges</th><th class="n">Transit rows</th><th class="n">Country pairs</th><th>Currencies</th><th>Valid from</th><th>Valid to</th><th>Last loaded</th></tr></thead><tbody>';
  for (const mode of ['Ground', 'Air']) {
    const list = C.filter(r => r[0] === mode); if (!list.length) continue;
    const t = [0, 0, 0, 0, 0];
    h += `<tr class="mode"><td colspan="12">${mode}</td></tr>`;
    for (const r of list) {
      const np = pairs[mode].get(r[1]) || 0; t[0] += r[2]; t[1] += r[3]; t[2] += r[9]; t[3] += r[10]; t[4] += r[11];
      h += `<tr><td><b>${esc(r[1])}</b></td><td class="small">${esc(r[4])}</td><td class="n">${fmt(r[2])}</td><td class="n">${fmt(r[3])}</td>` +
           `<td class="n">${r[9] ? fmt(r[9]) : ''}</td><td class="n">${r[10] ? fmt(r[10]) : ''}</td><td class="n">${r[11] ? fmt(r[11]) : ''}</td>` +
           `<td class="n">${np ? fmt(np) : ''}</td><td>${esc(r[5])}</td><td class="nw">${esc(r[6])}</td><td class="nw">${esc(r[7])}</td><td class="nw">${esc(r[8])}</td></tr>`;
    }
    h += `<tr class="tot"><td>${mode} total</td><td>${list.length} carriers</td><td class="n">${fmt(t[0])}</td><td class="n">${fmt(t[1])}</td><td class="n">${fmt(t[2])}</td>` +
         `<td class="n">${fmt(t[3])}</td><td class="n">${fmt(t[4])}</td><td class="n">${fmt((mode === 'Ground' ? g : a).size)}</td><td colspan="4"></td></tr>`;
  }
  document.getElementById('carriers').innerHTML = h + '</tbody>';
})();

// ---------- map
const svg = d3.select('#map'), W = 960, H = 500;
svg.attr('viewBox', `0 0 ${W} ${H}`);
const feats = topojson.feature(TH_WORLD, TH_WORLD.objects.countries).features.filter(f => f.id !== '010');
feats.forEach(f => f.cc = TH_NUM[f.id]);
const proj = d3.geoNaturalEarth1().fitExtent([[4, 4], [W - 4, H - 4]], {type: 'FeatureCollection', features: feats});
const path = d3.geoPath(proj);
const root = svg.append('g');
root.append('path').attr('d', path({type: 'Sphere'})).attr('fill', 'var(--sea)');
const cent = new Map(feats.map(f => [f.cc, proj(d3.geoCentroid(f))]));
// multi-part countries (FR, US, NO ...): arcs start at the largest part
for (const f of feats) if (f.geometry && f.geometry.type === 'MultiPolygon') {
  let best = null, area = -1;
  for (const poly of f.geometry.coordinates) { const pf = {type: 'Polygon', coordinates: poly}; const ar = d3.geoArea(pf); if (ar > area) { area = ar; best = pf; } }
  cent.set(f.cc, proj(d3.geoCentroid(best)));
}
const countries = root.append('g').selectAll('path').data(feats).join('path').attr('class', 'country').attr('d', path);
const arcs = root.append('g');
svg.call(d3.zoom().scaleExtent([1, 12]).on('zoom', e => {
  root.attr('transform', e.transform);
  root.selectAll('.country').attr('stroke-width', .4 / e.transform.k);
  arcs.selectAll('path').attr('stroke-width', function () { return this.dataset.w / e.transform.k; });
}));

let mode = 'GROUND', sel = null, dir = 'out';
const tip = document.getElementById('tip');
const cur = () => mode === 'GROUND' ? g : a;
const curStats = () => mode === 'GROUND' ? gStats : aStats;
function fillFor(f) {
  const s = curStats().get(f.cc); if (!s) return 'var(--land)';
  if (mode === 'GROUND') return s.best >= 3 ? 'var(--pc)' : s.best === 2 ? 'var(--city)' : 'var(--zone)';
  return s.best === 4 ? 'var(--a4)' : s.best === 3 ? 'var(--a3)' : s.best === 2 ? 'var(--a2)' : 'var(--a1)';
}
function legend() {
  const none = '<div><i style="background:var(--land);border:1px solid var(--line)"></i>no ' + (mode === 'GROUND' ? 'ground' : 'air') + ' rates</div>';
  document.getElementById('legend').innerHTML = mode === 'GROUND'
    ? '<b>Ground: finest detail priced</b><div><i style="background:var(--pc)"></i>postcode → postcode</div><div><i style="background:var(--city)"></i>city → city</div><div><i style="background:var(--zone)"></i>zone / country only</div>' + none
    : '<b>Air: finest detail priced</b><div><i style="background:var(--a4)"></i>airport → airport</div><div><i style="background:var(--a3)"></i>postcode zone</div><div><i style="background:var(--a2)"></i>city → city</div><div><i style="background:var(--a1)"></i>zone / country only</div>' + none;
}
function stats() {
  const st = (v, l) => `<div class="stat"><b>${fmt(v)}</b><span>${l}</span></div>`;
  const c = [0, 0, 0, 0, 0]; let n = 0; cur().forEach(p => { c[p.best]++; n += p.n; });
  document.getElementById('stats').innerHTML = mode === 'GROUND'
    ? st(g.size, 'country pairs') + st(c[3], 'postcode → postcode') + st(n, 'postcode-area lanes') + st(c[2], 'city → city only') + st(c[1], 'zone / country only')
    : st(a.size, 'country pairs') + st(c[4], 'airport → airport') + st(n, 'airport-pair lanes') + st(c[3], 'postcode zone') + st(c[2], 'city → city') + st(c[1], 'zone / country only');
}
function arc(o, d) {
  const p0 = cent.get(o), p1 = cent.get(d); if (!p0 || !p1) return null;
  const [x0, y0] = p0, [x1, y1] = p1, dr = Math.hypot(x1 - x0, y1 - y0) * 1.3;
  return `M${x0},${y0}A${dr},${dr} 0 0,1 ${x1},${y1}`;
}
function partners() {
  const out = [];
  cur().forEach(p => { if ((dir === 'out' && p.o === sel) || (dir === 'in' && p.d === sel)) out.push({c: dir === 'out' ? p.d : p.o, p}); });
  return out.sort((x, y) => (y.p.best - x.p.best) || (y.p.carriers.size - x.p.carriers.size) || name(x.c).localeCompare(name(y.c)));
}
function drawArcs() {
  arcs.selectAll('*').remove(); if (!sel) return;
  const k = d3.zoomTransform(svg.node()).k;
  for (const {c, p} of partners()) {
    const dd = arc(dir === 'out' ? sel : c, dir === 'out' ? c : sel); if (!dd) continue;
    const col = mode === 'GROUND' ? (p.best >= 3 ? 'var(--pc)' : p.best === 2 ? 'var(--city)' : 'var(--arc)')
                                  : (p.best === 4 ? 'var(--a4)' : p.best >= 2 ? 'var(--a3)' : 'var(--arc)');
    const w = mode === 'GROUND' ? (p.best >= 3 ? 1.8 : 1.1) : Math.min(.6 + p.carriers.size * .45, 3.5);
    arcs.append('path').attr('class', 'arc').attr('d', dd).attr('stroke', col).attr('data-w', w).attr('stroke-width', w / k);
  }
}
const lvShort = lv => ({POSTCODE: 'postcode', CITY: 'city', COUNTRY: 'zone', AIRPORT: 'airport'}[lv]);
function panel() {
  const P = document.getElementById('panel');
  if (!sel) {
    P.innerHTML = mode === 'GROUND'
      ? '<h3>Ground coverage</h3><p class="muted">Pick a country on the map. Dark green countries have postcode-level lanes; the table then lists every connected country, the carriers and how many postcode-area lanes each pair has.</p>' + topZip()
      : '<h3>Air coverage</h3><p class="muted">Pick a country on the map to see every country it is priced to or from, at what detail (airport pair, postcode zone, city or country zone) and by which carriers.</p>';
    return;
  }
  const list = partners(), s = curStats().get(sel);
  let h = `<h3>${esc(name(sel))} <span class="muted" style="font-weight:400">(${sel})</span></h3>` +
          `<div class="seg" role="group" aria-label="Direction"><button type="button" data-dir="out" aria-pressed="${dir === 'out'}">From ${sel} (${s ? s.out : 0})</button>` +
          `<button type="button" data-dir="in" aria-pressed="${dir === 'in'}">To ${sel} (${s ? s.in : 0})</button></div>`;
  if (!list.length) { P.innerHTML = h + '<p class="muted">No rates in this direction.</p>'; bindDir(); return; }
  h += `<table><thead><tr><th>${dir === 'out' ? 'To' : 'From'}</th><th>Detail</th><th>Carriers</th><th class="n">${mode === 'GROUND' ? 'Postcode lanes' : 'Airport pairs'}</th></tr></thead><tbody>`;
  for (const {c, p} of list) {
    const cars = [...p.carriers.entries()].map(([k, v]) => k + (v !== p.lvl ? ' (' + lvShort(v) + ')' : '')).join(', ');
    let n = p.n ? fmt(p.n) : '';
    if (mode === 'GROUND') { const z = zipMap.get(p.o + '>' + p.d); if (z) n = fmt(z[2]) + `<div class="codes">${z[3]} → ${z[4]} areas</div>`; }
    h += `<tr><td>${esc(name(c))}</td><td><span class="lv ${p.lvl}">${lvShort(p.lvl)}</span></td><td class="codes">${cars}</td><td class="n">${n}</td></tr>`;
  }
  P.innerHTML = h + '</tbody></table>'; bindDir();
}
function topZip() {
  const rows = [...DATA.zip].sort((x, y) => y[2] - x[2]).slice(0, 15);
  return '<table><thead><tr><th>Postcode-level pair</th><th class="n">Lanes</th><th>Carriers</th></tr></thead><tbody>' +
    rows.map(z => `<tr><td>${z[0]} → ${z[1]}</td><td class="n">${fmt(z[2])}</td><td class="codes">${esc(z[5])}</td></tr>`).join('') + '</tbody></table>';
}
function bindDir() { document.querySelectorAll('#panel [data-dir]').forEach(b => b.onclick = () => { dir = b.dataset.dir; drawArcs(); panel(); }); }
function render() { countries.style('fill', fillFor).classed('sel', f => f.cc === sel); legend(); stats(); drawArcs(); panel(); }
countries.on('click', (e, f) => { sel = sel === f.cc ? null : f.cc; render(); })
  .on('mousemove', (e, f) => {
    const s = curStats().get(f.cc), box = document.getElementById('mapbox').getBoundingClientRect();
    tip.hidden = false; tip.style.left = (e.clientX - box.left + 12) + 'px'; tip.style.top = (e.clientY - box.top + 12) + 'px';
    tip.textContent = name(f.cc || '') + (s ? ` · ${s.out} to, ${s.in} from · best: ` + (mode === 'GROUND' ? GLVL[['', 'COUNTRY', 'CITY', 'POSTCODE'][Math.min(s.best, 3)]] : ALVL[s.best]) : ' · no rates');
  })
  .on('mouseleave', () => tip.hidden = true);
for (const [id, m] of [['tGround', 'GROUND'], ['tAir', 'AIR']]) document.getElementById(id).onclick = () => {
  mode = m; document.getElementById('tGround').setAttribute('aria-pressed', m === 'GROUND'); document.getElementById('tAir').setAttribute('aria-pressed', m === 'AIR'); render();
};

// ---------- footnote: what the map cannot show
const drawn = new Set(feats.map(f => f.cc)), used = new Set();
for (const p of [...g.values(), ...a.values()]) { used.add(p.o); used.add(p.d); }
const off = [...used].filter(c => !drawn.has(c)).sort();
const unplaced = DATA.unplaced.map(u => `${u[1]} ${u[0].toLowerCase()} ${fmt(u[2])}`).join(', ');
document.getElementById('foot').textContent =
  'Country pairs come from the freight rows and the zone charts; the detail shown is the finest level any carrier prices a pair at. ' +
  (off.length ? 'Too small for the map or non-standard codes (counted, not drawn): ' + off.map(c => name(c) + ' (' + c + ')').join(', ') + '. ' : '') +
  (unplaced ? 'Freight rows without a country pair (counted above, not on the map): ' + unplaced + '.' : '');
render();
})();
</script>
</asp:Content>
