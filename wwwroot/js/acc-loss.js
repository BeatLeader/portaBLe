/*
 * "Where accuracy is lost" / "Where players fail" views for the Leaderboard page (two tabs of one card).
 *
 * Reads the map's AccLossProfiles row (#acc-loss-profile) and the shared model row (#acc-loss-model).
 *
 * Accuracy tab (Analysis/py/export_acc_loss_profiles.py, model a17_acc_loss_model.py): per ~5 s section, the expected point loss of
 * every component (precision / swing angles / misses) at a base skill; skill scales each component by one factor, so any skill is
 * recomputed exactly. The total per skill follows the map's acc rating (latent model); the bottom-up model splits it over sections,
 * components and factors.
 *
 * Pass tab (Analysis/py/export_pass_profiles.py, pass rating v2 = analyzer PassEnergy): every swing's pass difficulty; the exact
 * energy-bar dynamic programming runs here for the chosen pass level: each note is missed with probability
 * sigmoid(slope * (ln PassDiff - skill)), energy starts at 50 %, +1 % per hit, -15 % per miss, the attempt fails at 0.
 */
(function () {
	'use strict';
	const root = document.querySelector('.acc-loss-component');
	const profileEl = document.getElementById('acc-loss-profile');
	const modelEl = document.getElementById('acc-loss-model');
	if (!root || !profileEl || !modelEl || typeof Chart === 'undefined') return;
	const P = JSON.parse(profileEl.textContent);
	const M = JSON.parse(modelEl.textContent);
	const W = P.windows;
	const COMP = M.components;
	const COLORS = {precision: '#4ea1ff', swing: '#ffb347', misses: '#ff5c5c'};
	const fmtTime = t => `${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}`;
	const esc = s => String(s).replace(/[&<>"']/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'}[c]));

	// ---------------------------------------------------------------- hints: what every factor means (keyed by the label before ':')
	const HINTS = {
		'Fast swings': "The analyzer's swing speed: how many times per second this hand swings, scaled up (at most x2) when the hand travels far between notes. 9–11 means roughly 5–11 swings per second for one hand.",
		'Little time between swings of a hand': "Seconds since the same hand's previous swing. Under 0.1 s means more than 10 swings per second with one hand.",
		'Dense timing (both hands)': 'Seconds since the previous note of either hand: small values mean notes follow each other quickly across both hands.',
		'Direction change from the last swing': "Angle between this swing and the same hand's previous one. 'Straight back' is the natural up-down alternation, 'same direction' is a reset, 'right angle' and 'sharp turn' are awkward transitions.",
		'Hand movement between swings': "How far (in lanes) the hand moves from its previous note. Under 0.5 lanes means the next note is in the same spot, which gives small swings that lose angle points; 3 or more means crossing the grid.",
		'Angle strain': "How far the swing's angle is from the hand's comfortable angle (analyzer measure; 0 = natural, higher = more wrist rotation).",
		'Repositioning': 'How far the hand has to move between the end of its last swing and the start of this one (analyzer measure, roughly grid units).',
		'Hit distance': "Distance between the hand's consecutive hit points (analyzer measure, grid units).",
		'Cut direction': 'Arrow direction. Vertical (up/down) is the baseline; horizontal, diagonal and dot notes change how points are lost.',
		'Lane': 'Outer lanes (leftmost / rightmost column) compared with the two inner columns.',
		'Row': 'Top, middle or bottom row of the grid; the bottom row is the baseline.',
		'Crossover': 'A red note in the rightmost lane or a blue note in the leftmost lane: the arms have to cross.',
		'Multi-note pattern': 'Several notes cut by one swing: stacks, sliders, windows, towers.',
		'Chain head': 'The first note of a chain (burst slider).',
		'Parity break (reset)': 'The analyzer expects a reset here: two swings in the same direction, so the hand has to come back without cutting.',
		'Bomb avoidance': 'The swing has to avoid a bomb.',
		'Walls nearby': 'A dodge or crouch wall is active around this swing.',
		'Note jump speed': 'How fast notes fly towards the player, in m/s.',
		'Jump distance': 'How far away notes appear, in meters. A short jump distance leaves less time to read the pattern.',
		'Density (swings within 2 s)': 'How many swings (both hands) happen within 2 seconds before and after this one.',
		'Minutes into the map': 'How far into the map the swing is.',
		// pass tab
		'Swing speed': "How fast the hand has to swing: swings per second, scaled up for long movements between notes. The core of the pass rating.",
		'Tech (angle strain, repositioning, rotation)': 'Awkward angles, long repositioning and wrist rotation. Pass rating v2 weighs these 6.5x more than the classic rating did, because players fail tech maps far more often than their old pass rating said.',
		'Crossovers': 'A red note in the rightmost lane or a blue note in the leftmost lane: the arms cross. Each such swing counts 1.59x as hard to pass, the biggest single thing the classic rating missed.',
		'Horizontal cuts': 'Left / right arrows count 1.37x as hard to pass.',
		'Diagonal cuts': 'Diagonal arrows count 1.29x as hard to pass.',
		'High note jump speed': 'Very fast notes are harder to react to (the analyzer NJS buff).',
		'Hand alternation (streams)': 'Notes alternating between hands get a small bonus (x1.05).',
		'Walls': 'Swings during dodge or crouch walls count up to 1.2x as hard.',
		// accuracy components
		'Precision (centre cut)': 'The 15 points for how close to the centre of the note you cut.',
		'Swing angles (pre/post)': 'The 70 + 30 points for swinging far enough before and after the cut.',
		'Misses / bad cuts': 'Notes missed or cut in the wrong direction / with the wrong saber: all 115 points lost.',
	};
	const hintFor = label => HINTS[label] || HINTS[String(label).split(':')[0]] || '';
	const info = label => {
		const h = hintFor(label);
		return h ? ` <span class="acc-loss-info" title="${esc(h)}">ⓘ</span>` : '';
	};
	function glossary(labels) {
		const seen = new Set(), items = [];
		for (const l of labels) {
			const key = HINTS[l] ? l : String(l).split(':')[0];
			if (seen.has(key) || !HINTS[key]) continue;
			seen.add(key); items.push(`<dt>${esc(key)}</dt><dd>${esc(HINTS[key])}</dd>`);
		}
		return items.length ? `<details class="acc-loss-glossary"><summary>What do these mean?</summary><dl>${items.join('')}</dl></details>` : '';
	}

	// ---------------------------------------------------------------- styles + skeleton
	if (!document.getElementById('acc-loss-styles')) {
		const st = document.createElement('style');
		st.id = 'acc-loss-styles';
		st.textContent = `
		.acc-loss-component { margin: 1em 0; padding: 1em; background-color: #2b2b2b; color: #fff; font-size: 0.85em; border-radius: 0.4em; }
		.acc-loss-tabs { display: flex; gap: 0.3em; border-bottom: 1px solid #444; margin-bottom: 0.6em; }
		.acc-loss-tabs button { background: none; color: #bbb; border: none; border-bottom: 2px solid transparent; padding: 0.35em 0.8em; cursor: pointer; font-weight: bold; font-size: 1.05em; }
		.acc-loss-tabs button.selected { color: #fff; border-bottom-color: #4ea1ff; }
		.acc-loss-sub { color: #aaa; font-size: 0.85em; }
		.acc-loss-skills { display: flex; flex-wrap: wrap; gap: 0.4em; margin: 0.6em 0; }
		.acc-loss-skills button { background: #4e4e4e; color: #fff; border: none; border-radius: 0.3em; padding: 0.25em 0.6em; cursor: pointer; }
		.acc-loss-skills button.selected { background: #838383; }
		.acc-loss-skills button.replay { background: #3d4b3d; }
		.acc-loss-skills button.replay.selected { background: #5f7a5f; }
		.acc-loss-skills small { color: #ccc; margin-left: 0.3em; }
		.acc-loss-summary { margin: 0.3em 0 0.6em; }
		.acc-loss-summary .sw { display: inline-block; width: 0.8em; height: 0.8em; border-radius: 0.15em; margin: 0 0.25em 0 0.6em; vertical-align: -0.05em; }
		.acc-loss-canvas { position: relative; height: 260px; }
		.acc-loss-factors { margin-top: 0.8em; display: grid; grid-template-columns: minmax(12em, 24em) 1fr 4em; gap: 0.25em 0.6em; align-items: center; }
		.acc-loss-factors .bar { height: 0.75em; background: #6c8ebf; border-radius: 0.2em; }
		.acc-loss-factors .bar.pass { background: #d9776b; }
		.acc-loss-factors .num { text-align: right; color: #ccc; }
		.acc-loss-info { color: #8fb4ff; cursor: help; font-size: 0.95em; }
		.acc-loss-glossary { margin-top: 0.6em; color: #ccc; }
		.acc-loss-glossary summary { cursor: pointer; color: #8fb4ff; }
		.acc-loss-glossary dl { display: grid; grid-template-columns: minmax(10em, 18em) 1fr; gap: 0.3em 0.8em; margin: 0.5em 0 0; }
		.acc-loss-glossary dt { color: #fff; font-weight: bold; }
		.acc-loss-glossary dd { margin: 0; }
		.acc-loss-note { color: #999; font-size: 0.8em; margin-top: 0.6em; }`;
		document.head.appendChild(st);
	}
	const hasPass = !!P.pass && !!M.pass;
	root.innerHTML = `
		<div class="acc-loss-tabs">
			<button type="button" data-tab="acc" class="selected">Where accuracy is lost</button>
			${hasPass ? '<button type="button" data-tab="pass">Where players fail</button>' : ''}
		</div>
		<div class="acc-loss-pane" data-pane="acc">
			<div class="acc-loss-sub">expected points lost per note, by section, for players of a given skill</div>
			<div class="acc-loss-skills"></div>
			<div class="acc-loss-summary"></div>
			<div class="acc-loss-canvas"><canvas></canvas></div>
			<div class="acc-loss-factors"></div>
			<div class="acc-loss-gloss"></div>
			<div class="acc-loss-note"></div>
		</div>
		${hasPass ? `<div class="acc-loss-pane" data-pane="pass" style="display:none">
			<div class="acc-loss-sub">chance of failing in each section, for a player who has reached it, by pass level</div>
			<div class="acc-loss-skills"></div>
			<div class="acc-loss-summary"></div>
			<div class="acc-loss-canvas"><canvas></canvas></div>
			<div class="acc-loss-factors"></div>
			<div class="acc-loss-gloss"></div>
			<div class="acc-loss-note"></div>
		</div>` : ''}`;
	const pane = name => root.querySelector(`[data-pane="${name}"]`);
	const labels = W.t0.map(t => fmtTime(t));

	// ================================================================ accuracy tab
	(function accTab() {
		const el = pane('acc');
		const interp = (x, xs, ys) => {
			if (x <= xs[0]) return ys[0];
			if (x >= xs[xs.length - 1]) return ys[ys.length - 1];
			for (let i = 1; i < xs.length; i++) {
				if (x <= xs[i]) return ys[i - 1] + (ys[i] - ys[i - 1]) * (x - xs[i - 1]) / (xs[i] - xs[i - 1]);
			}
			return ys[ys.length - 1];
		};
		const term = (c, s) => interp(s, M.skill_mid, M.skill_coef[c]) + interp(s, M.skill_mid, M.calibration);
		const mult = s => Object.fromEntries(COMP.map(c => [c, Math.exp(term(c, s) - term(c, M.base_skill))]));
		const totalNotes = W.n.reduce((a, b) => a + b, 0);
		const predictedAcc = parseFloat(root.dataset.predictedAcc);
		const anchored = predictedAcc > 0 && predictedAcc < 1 && M.reference_skill != null;
		// skill sensitivity of the map's characteristic (One Saber < 1: error rates fall more slowly with skill)
		const beta = (M.mode_skill_scale || {})[root.dataset.mode || ''] || 1;
		const modelLost = s => {
			const m = mult(s);
			let lost = 0;
			for (const c of COMP) lost += W[c].reduce((a, b) => a + b, 0) * m[c];
			return lost / totalNotes;
		};
		const expectedAcc = s => anchored
			? 1 - Math.min(1, Math.exp(Math.log(1 - predictedAcc) + beta * (M.reference_skill - s)))
			: 1 - modelLost(s);
		const scaleAt = s => (anchored ? (1 - expectedAcc(s)) / modelLost(s) : 1);
		const pctLabel = p => `Top ${+(100 - 100 * p).toFixed(1)}%`;
		const skillToPct = s => {
			const ks = Object.keys(M.percentiles).map(Number).sort((a, b) => a - b);
			return interp(s, ks.map(k => M.percentiles[String(k)]), ks);
		};
		const skillsEl = el.querySelector('.acc-loss-skills'), summaryEl = el.querySelector('.acc-loss-summary');
		const factorsEl = el.querySelector('.acc-loss-factors'), glossEl = el.querySelector('.acc-loss-gloss');
		const choices = Object.keys(M.percentiles).map(Number).sort((a, b) => a - b)
			.map(p => ({label: pctLabel(p), skill: M.percentiles[String(p)], replay: null}));
		for (const st of ['top', 'mid']) {
			const o = P.observed && P.observed[st];
			if (!o || o.skill == null || !o.loss.some(v => v != null)) continue;
			choices.push({label: st === 'top' ? `Best replays (${o.replays})` : `Mid-field replays (${o.replays})`, skill: o.skill, replay: st});
		}
		let current = choices.find(c => !c.replay && Math.abs(c.skill - M.base_skill) < 1e-3) || choices[0];
		const chart = new Chart(el.querySelector('canvas').getContext('2d'), {
			type: 'bar', data: {labels, datasets: []},
			options: {
				responsive: true, maintainAspectRatio: false, animation: false, interaction: {mode: 'index', intersect: false},
				scales: {
					x: {stacked: true, ticks: {color: '#bbb', maxTicksLimit: 16}, grid: {color: '#3a3a3a'}},
					y: {stacked: true, beginAtZero: true, ticks: {color: '#bbb', callback: v => `${v}%`},
						title: {display: true, text: 'points lost per note', color: '#bbb'}, grid: {color: '#3a3a3a'}},
				},
				plugins: {
					legend: {labels: {color: '#ddd'}},
					tooltip: {callbacks: {
						title: items => { const i = items[0].dataIndex; return `${fmtTime(W.t0[i])}–${fmtTime(W.t0[i] + W.w)} · ${Math.round(W.n[i])} notes`; },
						label: item => `${item.dataset.label}: ${item.parsed.y.toFixed(2)}%`,
						afterBody: items => {
							const top = W.top[items[0].dataIndex] || [];
							return top.length ? ['', 'Biggest factors here:', ...top.map(([l, s]) => `  ${l} (${Math.round(100 * s)}% of the loss)`)] : [];
						},
					}},
				},
			},
		});
		function render() {
			const s = current.skill, k = scaleAt(s);
			const m = Object.fromEntries(Object.entries(mult(s)).map(([c, v]) => [c, v * k]));
			const perNote = c => W[c].map((v, i) => (W.n[i] > 0 ? (100 * v * m[c]) / W.n[i] : null));
			const ds = COMP.map(c => ({type: 'bar', label: M.labels[c], data: perNote(c), backgroundColor: COLORS[c], stack: 'model', order: 1, barPercentage: 1.0, categoryPercentage: 0.95}));
			if (current.replay) {
				const o = P.observed[current.replay];
				ds.push({type: 'line', label: `Replays (observed, ${o.replays} runs)`, data: o.loss.map(v => (v == null ? null : 100 * v)),
					borderColor: '#fff', backgroundColor: '#fff', pointRadius: 2.5, borderWidth: 2, spanGaps: false, stack: 'obs', order: 0});
			}
			chart.data.datasets = ds;
			chart.update();
			skillsEl.innerHTML = '';
			for (const c of choices) {
				const b = document.createElement('button');
				b.className = (c === current ? 'selected ' : '') + (c.replay ? 'replay' : '');
				b.innerHTML = `${c.label}<small>${(100 * expectedAcc(c.skill)).toFixed(2)}%</small>`;
				b.title = c.replay ? `model at the skill of these replays' players (about the top ${(100 - 100 * skillToPct(c.skill)).toFixed(1)}% of the playerbase), next to what those replays actually lost` : 'expected accuracy at this level of the playerbase';
				b.onclick = () => { current = c; render(); };
				skillsEl.appendChild(b);
			}
			const lost = {}; let all = 0;
			for (const c of COMP) { lost[c] = W[c].reduce((a, b) => a + b, 0) * m[c] / totalNotes; all += lost[c]; }
			summaryEl.innerHTML = `Expected accuracy <b>${(100 * (1 - all)).toFixed(2)}%</b> · lost: ` +
				COMP.map(c => `<span class="sw" style="background:${COLORS[c]}"></span>${M.labels[c]} ${(100 * lost[c]).toFixed(2)}%${info(M.labels[c])}`).join('');
			const fac = {}; let base = 0;
			for (const c of COMP) {
				base += P.base[c] * m[c];
				for (const [key, v] of Object.entries(P.factors[c] || {})) fac[key] = (fac[key] || 0) + v * m[c];
			}
			const total = base + Object.values(fac).reduce((a, b) => a + b, 0);
			const rows = Object.entries(fac).filter(([, v]) => v / total >= 0.005).sort((a, b) => b[1] - a[1]).slice(0, 10);
			const maxv = Math.max(...rows.map(r => r[1]), 1e-9);
			factorsEl.innerHTML = `<div><b>Biggest factors on this map</b></div><div class="acc-loss-sub">${(100 * base / total).toFixed(0)}% of the lost points ` +
				(rows.length ? 'would be lost on typical swings anyway; the rest comes from:' : 'would be lost on typical swings anyway: nothing here is harder than a typical swing.') +
				`</div><div class="num">${rows.length ? 'share' : ''}</div>` +
				rows.map(([l, v]) => `<div>${esc(l)}${info(l)}</div><div><div class="bar" style="width:${(100 * v / maxv).toFixed(1)}%"></div></div>` +
					`<div class="num">${(100 * v / total).toFixed(1)}%</div>`).join('');
			glossEl.innerHTML = glossary([...rows.map(r => r[0]), ...COMP.map(c => M.labels[c])]);
		}
		const v = M.validation || {};
		el.querySelector('.acc-loss-note').textContent =
			"Bottom-up model: every swing's expected loss is a product of named factors (speed, timing, direction change, crossovers, " +
			'cut direction, lane/row, patterns, NJS, jump distance, density, ...) times a skill curve, fitted on replays of other songs. ' +
			(anchored ? "The total per skill level follows this map's acc rating; the model splits it over sections and causes. " : '') +
			(v.where_median_spearman ? `On held-out maps it ranks sections like the replays do with a median Spearman of ` +
				`${v.where_median_spearman.top?.model ?? '?'} (best replays) / ${v.where_median_spearman.mid?.model ?? '?'} (mid-field); ` +
				`the replays agree with themselves at ${v.where_median_spearman.top?.split_half ?? '?'} / ${v.where_median_spearman.mid?.split_half ?? '?'}.` : '');
		render();
	})();

	// ================================================================ pass tab
	let passTab = null;
	function buildPassTab() {
		const el = pane('pass'), PP = P.pass, PM = M.pass;
		// decode swings: [ln PassDiff byte, notes] per swing
		const raw = atob(PP.q), n = raw.length / 2;
		const ld = new Float64Array(n), notes = new Uint8Array(n);
		for (let i = 0; i < n; i++) { ld[i] = raw.charCodeAt(2 * i) * PM.qs + PM.q0; notes[i] = raw.charCodeAt(2 * i + 1); }
		const winOf = new Int32Array(n);
		{ let i = 0; PP.win.forEach((cnt, w) => { for (let j = 0; j < cnt; j++) winOf[i++] = w; }); }
		const nw = PP.win.length, factorScale = PM.scale * (PP.one_saber ? PM.one_saber : 1);
		// exact energy-bar DP: clear chance, and per section the mass alive at its start and the mass that fails in it
		function simulate(skill) {
			let E = new Float64Array(101), N = new Float64Array(101);
			E[PM.start] = 1;
			const alive0 = new Float64Array(nw), dead = new Float64Array(nw);
			let lastWin = -1;
			for (let i = 0; i < n; i++) {
				const w = winOf[i];
				if (w !== lastWin) { let a = 0; for (let k = 1; k <= 100; k++) a += E[k]; for (let x = lastWin + 1; x <= w; x++) alive0[x] = a; lastWin = w; }
				const p = 1 / (1 + Math.exp(-PM.slope * (ld[i] - skill)));
				for (let r = 0; r < notes[i]; r++) {
					N.fill(0);
					for (let k = 1; k <= 100; k++) {
						const m = E[k];
						if (m === 0) continue;
						N[Math.min(k + PM.hit, 100)] += m * (1 - p);
						if (k - PM.miss > 0) N[k - PM.miss] += m * p; else dead[w] += m * p;
					}
					const t = E; E = N; N = t;
				}
			}
			let clear = 0; for (let k = 1; k <= 100; k++) clear += E[k];
			return {clear, alive0, dead};
		}
		const skillOf = level => Math.log(level / factorScale);
		const levelOf = skill => Math.exp(skill) * factorScale;
		const rated = levelOf(PP.skill50);
		const cache = new Map();
		const sim = skill => { const key = skill.toFixed(4); if (!cache.has(key)) cache.set(key, simulate(skill)); return cache.get(key); };
		const choices = [];
		for (const d of [-3, -2, -1, 0, 1, 2, 3]) {
			const level = d === 0 ? rated : Math.round(rated) + d;
			if (level < 0.3) continue;
			choices.push({label: d === 0 ? `Rated ${rated.toFixed(2)}★` : `${level}★`, skill: skillOf(level), observed: false,
				title: d === 0 ? "a player at this map's own pass rating: clears it half the time" : `a player whose pass level is ${level}★ (clears ${level}★ maps half the time)`});
		}
		const O = PP.observed;
		// real attempts are a mix of players (weaker ones fail early): skills ~ Normal(mu, spread), 9-point Gauss-Hermite
		const GH_X = [-4.512746, -3.205429, -2.076848, -1.023256, 0, 1.023256, 2.076848, 3.205429, 4.512746];
		const GH_W = [0.000022, 0.002789, 0.049916, 0.244098, 0.406349, 0.244098, 0.049916, 0.002789, 0.000022];
		const spread = PM.attempt_spread ?? 0.3;
		const mixCache = new Map();
		function simMix(mu) {
			const key = mu.toFixed(4);
			if (mixCache.has(key)) return mixCache.get(key);
			const out = {clear: 0, alive0: new Float64Array(nw), dead: new Float64Array(nw)};
			GH_X.forEach((x, j) => {
				if (GH_W[j] < 1e-3) return;                              // the outermost nodes carry < 0.1 % of the weight
				const r = sim(mu + spread * x), w = GH_W[j] / 0.9994;
				out.clear += w * r.clear;
				for (let i = 0; i < nw; i++) { out.alive0[i] += w * r.alive0[i]; out.dead[i] += w * r.dead[i]; }
			});
			mixCache.set(key, out);
			return out;
		}
		if (O && O.clear_rate != null && O.clear_rate > 0.001 && O.clear_rate < 0.999) {
			let lo = -3, hi = 6;                                     // mix centre at which the model clears as often as the real attempts do
			for (let it = 0; it < 16; it++) { const mid = (lo + hi) / 2; if (simMix(mid).clear >= O.clear_rate) hi = mid; else lo = mid; }
			choices.push({label: 'Typical attempt', skill: (lo + hi) / 2, observed: true, mix: true,
				title: `a mix of players (pass levels spread like real attempts) that clears as often as the ${O.attempts?.toLocaleString?.() ?? ''} real clean attempts did, next to where those attempts actually failed`});
		}
		let current = (location.hash === '#pass-attempts' && choices.find(c => c.observed)) || choices.find(c => c.label.startsWith('Rated')) || choices[0];
		const skillsEl = el.querySelector('.acc-loss-skills'), summaryEl = el.querySelector('.acc-loss-summary');
		const factorsEl = el.querySelector('.acc-loss-factors'), glossEl = el.querySelector('.acc-loss-gloss');
		const chart = new Chart(el.querySelector('canvas').getContext('2d'), {
			type: 'bar', data: {labels, datasets: []},
			options: {
				responsive: true, maintainAspectRatio: false, animation: false, interaction: {mode: 'index', intersect: false},
				scales: {
					x: {ticks: {color: '#bbb', maxTicksLimit: 16}, grid: {color: '#3a3a3a'}},
					y: {beginAtZero: true, ticks: {color: '#bbb', callback: v => `${v}%`},
						title: {display: true, text: 'chance to fail here', color: '#bbb'}, grid: {color: '#3a3a3a'}},
				},
				plugins: {
					legend: {labels: {color: '#ddd'}},
					tooltip: {callbacks: {
						title: items => { const i = items[0].dataIndex; return `${fmtTime(W.t0[i])}–${fmtTime(W.t0[i] + W.w)} · ${PP.win[i]} swings`; },
						label: item => `${item.dataset.label}: ${item.parsed.y == null ? '–' : item.parsed.y.toFixed(2) + '%'}`,
						afterBody: items => {
							const i = items[0].dataIndex, r = current.mix ? simMix(current.skill) : sim(current.skill), top = PP.top[i] || [];
							const lines = [`Still playing at the start of this section: ${(100 * r.alive0[i]).toFixed(1)}%`];
							return top.length ? [...lines, '', 'What makes it hard:', ...top.map(([l, s]) => `  ${l} (${Math.round(100 * s)}%)`)] : lines;
						},
					}},
				},
			},
		});
		function render() {
			const r = current.mix ? simMix(current.skill) : sim(current.skill);
			const risk = Array.from({length: nw}, (_, i) => (r.alive0[i] > 1e-9 ? 100 * r.dead[i] / r.alive0[i] : null));
			const ds = [{type: 'bar', label: 'Model: chance to fail in this section', data: risk, backgroundColor: '#d9776b', order: 1, barPercentage: 1.0, categoryPercentage: 0.95}];
			if (current.observed && O) {
				ds.push({type: 'line', label: `Real attempts (observed)`, data: O.hazard.map(v => (v == null ? null : 100 * v)),
					borderColor: '#fff', backgroundColor: '#fff', pointRadius: 2.5, borderWidth: 2, spanGaps: false, order: 0});
			}
			chart.data.datasets = ds;
			chart.update();
			skillsEl.innerHTML = '';
			for (const c of choices) {
				const b = document.createElement('button');
				b.className = (c === current ? 'selected ' : '') + (c.observed ? 'replay' : '');
				b.innerHTML = `${esc(c.label)}<small>${Math.round(100 * (c.mix ? simMix(c.skill) : sim(c.skill)).clear)}% clear</small>`;
				b.title = c.title;
				b.onclick = () => { current = c; render(); };
				skillsEl.appendChild(b);
			}
			let worst = 0; for (let i = 1; i < nw; i++) if ((risk[i] ?? -1) > (risk[worst] ?? -1)) worst = i;
			const failTotal = r.dead.reduce((a, b) => a + b, 0);
			let firstMin = 0; for (let i = 0; i < nw; i++) if (W.t0[i] - W.t0[0] < 60) firstMin += r.dead[i];
			summaryEl.innerHTML = `Chance to clear <b>${(100 * r.clear).toFixed(0)}%</b>` +
				(failTotal > 1e-6 ? ` · riskiest section <b>${fmtTime(W.t0[worst])}–${fmtTime(W.t0[worst] + W.w)}</b> (${risk[worst].toFixed(1)}% of players reaching it fail there)` +
					` · ${(100 * firstMin / failTotal).toFixed(0)}% of fails happen in the first minute` : '');
			const rows = Object.entries(PP.factors || {}).sort((a, b) => b[1] - a[1]).slice(0, 8);
			const maxv = Math.max(...rows.map(x => x[1]), 1e-9);
			factorsEl.innerHTML = '<div><b>What makes this map hard to pass</b></div><div class="acc-loss-sub">share of the extra difficulty above a typical swing, ' +
				'weighted by how likely each swing is to cause a miss at this map\'s pass level</div><div class="num">share</div>' +
				rows.map(([l, v]) => `<div>${esc(l)}${info(l)}</div><div><div class="bar pass" style="width:${(100 * v / maxv).toFixed(1)}%"></div></div>` +
					`<div class="num">${(100 * v).toFixed(0)}%</div>`).join('');
			glossEl.innerHTML = glossary(rows.map(x => x[0]));
		}
		el.querySelector('.acc-loss-note').textContent =
			'Pass rating v2 (energy bar): every note is missed with a probability that grows with its swing difficulty (speed, tech, crossovers, ' +
			'cut direction, NJS, walls) relative to the player\'s pass level; the game\'s energy bar starts at 50 %, gains 1 % per hit and loses 15 % per miss, ' +
			'and the attempt fails at 0. The pass rating is the level at which a player clears the map half the time. Measured against 24 M attempts it ' +
			'explains pass difficulty with R² 0.917 (classic rating 0.856) and locates the sections where players fail better than swing difficulty alone. ' +
			"'Typical attempt' models a mix of players, as real attempts are (weaker players fail early, so later sections see stronger ones), " +
			'and its white line shows where real clean attempts failed (quits and restarts count as still playing until they end).';
		render();
		return {chart};
	}

	// tabs (#pass in the URL opens the pass tab, #pass-attempts with the real-attempts overlay)
	const show = tab => {
		root.querySelectorAll('.acc-loss-tabs button').forEach(x => x.classList.toggle('selected', x.dataset.tab === tab));
		root.querySelectorAll('.acc-loss-pane').forEach(p => p.style.display = p.dataset.pane === tab ? '' : 'none');
		if (tab === 'pass' && !passTab) passTab = buildPassTab();
	};
	root.querySelectorAll('.acc-loss-tabs button').forEach(b => b.onclick = () => show(b.dataset.tab));
	if (hasPass && location.hash.startsWith('#pass')) show('pass');
})();
