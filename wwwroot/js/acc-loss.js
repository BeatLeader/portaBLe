/*
 * "Where accuracy is lost" view for the Leaderboard page.
 *
 * Reads the map's AccLossProfiles row (#acc-loss-profile) and the shared model row (#acc-loss-model), both written by
 * Analysis/py/export_acc_loss_profiles.py from the bottom-up model of Analysis/py/a17_acc_loss_model.py. The profile stores, per
 * ~5 s window, the expected point loss of every component (precision / swing angles / misses) at a base skill; skill scales each
 * component by one factor, exp(skill term(s) - skill term(base)), so any skill is recomputed exactly here.
 * The total per skill follows the map's acc rating (latent model: log(1 - acc(s)) = log(1 - predictedAcc) + reference skill - s);
 * the bottom-up model decides how that total splits over sections, components and factors.
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
	const modelLost = s => {
		const m = mult(s);
		let lost = 0;
		for (const c of COMP) lost += W[c].reduce((a, b) => a + b, 0) * m[c];
		return lost / totalNotes;
	};
	// expected accuracy at skill s: from the rating when available, else the bottom-up model's own total
	const expectedAcc = s => anchored
		? 1 - Math.min(1, Math.exp(Math.log(1 - predictedAcc) + M.reference_skill - s))
		: 1 - modelLost(s);
	// factor that scales the bottom-up losses so they add up to the expected total
	const scaleAt = s => (anchored ? (1 - expectedAcc(s)) / modelLost(s) : 1);
	const pctLabel = p => `Top ${+(100 - 100 * p).toFixed(1)}%`;
	const skillToPct = s => {
		const ks = Object.keys(M.percentiles).map(Number).sort((a, b) => a - b);
		const vs = ks.map(k => M.percentiles[String(k)] ?? M.percentiles[k.toString()]);
		return interp(s, vs, ks);
	};
	const fmtTime = t => `${Math.floor(t / 60)}:${String(Math.floor(t % 60)).padStart(2, '0')}`;

	// ---------------------------------------------------------------- styles + skeleton
	if (!document.getElementById('acc-loss-styles')) {
		const st = document.createElement('style');
		st.id = 'acc-loss-styles';
		st.textContent = `
		.acc-loss-component { margin: 1em 0; padding: 1em; background-color: #2b2b2b; color: #fff; font-size: 0.85em; border-radius: 0.4em; }
		.acc-loss-header { display: flex; justify-content: space-between; align-items: baseline; gap: 1em; flex-wrap: wrap; }
		.acc-loss-title { font-weight: bold; font-size: 1.1em; }
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
		.acc-loss-factors { margin-top: 0.8em; display: grid; grid-template-columns: minmax(12em, 22em) 1fr 4em; gap: 0.25em 0.6em; align-items: center; }
		.acc-loss-factors .bar { height: 0.75em; background: #6c8ebf; border-radius: 0.2em; }
		.acc-loss-factors .base { background: #555; }
		.acc-loss-factors .num { text-align: right; color: #ccc; }
		.acc-loss-note { color: #999; font-size: 0.8em; margin-top: 0.6em; }`;
		document.head.appendChild(st);
	}
	root.innerHTML = `
		<div class="acc-loss-header">
			<div><span class="acc-loss-title">Where accuracy is lost</span>
			<span class="acc-loss-sub">expected points lost per note, by section, for players of a given skill</span></div>
		</div>
		<div class="acc-loss-skills"></div>
		<div class="acc-loss-summary"></div>
		<div class="acc-loss-canvas"><canvas></canvas></div>
		<div class="acc-loss-factors"></div>
		<div class="acc-loss-note"></div>`;
	const skillsEl = root.querySelector('.acc-loss-skills');
	const summaryEl = root.querySelector('.acc-loss-summary');
	const factorsEl = root.querySelector('.acc-loss-factors');

	// skill choices: playerbase percentiles, plus the replay strata of this map (model at exactly their skill, with their losses)
	const choices = Object.keys(M.percentiles).map(Number).sort((a, b) => a - b)
		.map(p => ({key: `p${p}`, label: pctLabel(p), skill: M.percentiles[String(p)], replay: null}));
	for (const st of ['top', 'mid']) {
		const o = P.observed && P.observed[st];
		if (!o || o.skill == null || !o.loss.some(v => v != null)) continue;
		choices.push({key: `r${st}`, label: st === 'top' ? `Best replays (${o.replays})` : `Mid-field replays (${o.replays})`,
			skill: o.skill, replay: st});
	}
	let current = choices.find(c => !c.replay && Math.abs(c.skill - M.base_skill) < 1e-3) || choices[0];

	const ctx = root.querySelector('canvas').getContext('2d');
	const labels = W.t0.map(t => fmtTime(t));
	const chart = new Chart(ctx, {
		type: 'bar',
		data: {labels, datasets: []},
		options: {
			responsive: true, maintainAspectRatio: false, animation: false,
			interaction: {mode: 'index', intersect: false},
			scales: {
				x: {stacked: true, ticks: {color: '#bbb', maxTicksLimit: 16}, grid: {color: '#3a3a3a'}},
				y: {stacked: true, beginAtZero: true, ticks: {color: '#bbb', callback: v => `${v}%`},
					title: {display: true, text: 'points lost per note', color: '#bbb'}, grid: {color: '#3a3a3a'}},
			},
			plugins: {
				legend: {labels: {color: '#ddd'}},
				tooltip: {
					callbacks: {
						title: items => {
							const i = items[0].dataIndex;
							return `${fmtTime(W.t0[i])}–${fmtTime(W.t0[i] + W.w)} · ${Math.round(W.n[i])} notes`;
						},
						label: item => `${item.dataset.label}: ${item.parsed.y.toFixed(2)}%`,
						afterBody: items => {
							const top = W.top[items[0].dataIndex] || [];
							return top.length ? ['', 'Biggest factors here:', ...top.map(([l, s]) => `  ${l} (${Math.round(100 * s)}% of the loss)`)] : [];
						},
					},
				},
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
			b.title = c.replay ? `model at the skill of these replays' players (about the top ${(100 - 100 * skillToPct(c.skill)).toFixed(1)}% of the playerbase)` : 'expected accuracy at this level of the playerbase';
			b.onclick = () => { current = c; render(); };
			skillsEl.appendChild(b);
		}
		let lost = {}; let all = 0;
		for (const c of COMP) { lost[c] = W[c].reduce((a, b) => a + b, 0) * m[c] / totalNotes; all += lost[c]; }
		summaryEl.innerHTML = `Expected accuracy <b>${(100 * (1 - all)).toFixed(2)}%</b> · lost: ` +
			COMP.map(c => `<span class="sw" style="background:${COLORS[c]}"></span>${M.labels[c]} ${(100 * lost[c]).toFixed(2)}%`).join('');

		// map-wide breakdown: the plain-swing baseline plus each factor's excess, at this skill
		const fac = {};
		let base = 0;
		for (const c of COMP) {
			base += P.base[c] * m[c];
			for (const [k, v] of Object.entries(P.factors[c] || {})) fac[k] = (fac[k] || 0) + v * m[c];
		}
		const total = base + Object.values(fac).reduce((a, b) => a + b, 0);
		const rows = Object.entries(fac).filter(([, v]) => v / total >= 0.005).sort((a, b) => b[1] - a[1]).slice(0, 10);
		const maxv = Math.max(...rows.map(r => r[1]), 1e-9);
		factorsEl.innerHTML = `<div><b>Biggest factors on this map</b></div><div class="acc-loss-sub">${(100 * base / total).toFixed(0)}% of the lost points ` +
			(rows.length ? `would be lost on typical swings anyway; the rest comes from:` : `would be lost on typical swings anyway: nothing here is harder than a typical swing.`) +
			`</div><div class="num">${rows.length ? 'share' : ''}</div>` +
			rows.map(([l, v]) => `<div>${l}</div><div><div class="bar" style="width:${(100 * v / maxv).toFixed(1)}%"></div></div>` +
				`<div class="num">${(100 * v / total).toFixed(1)}%</div>`).join('');
	}
	const v = M.validation || {};
	root.querySelector('.acc-loss-note').textContent =
		'Bottom-up model: every swing\'s expected loss is a product of named factors (speed, timing, direction change, crossovers, ' +
		'cut direction, lane/row, patterns, NJS, jump distance, density, ...) times a skill curve, fitted on replays of other songs. ' +
		(anchored ? "The total per skill level follows this map's acc rating; the model splits it over sections and causes. " : '') +
		(v.where_median_spearman ? `On held-out maps it ranks sections like the replays do with a median Spearman of ` +
			`${v.where_median_spearman.top?.model ?? '?'} (best replays) / ${v.where_median_spearman.mid?.model ?? '?'} (mid-field); ` +
			`the replays agree with themselves at ${v.where_median_spearman.top?.split_half ?? '?'} / ${v.where_median_spearman.mid?.split_half ?? '?'}.` : '');
	render();
})();
