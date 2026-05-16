/*
 * PP Curve component for the Leaderboard page.
 * Vanilla-JS port of the BeatLeader Svelte `PpCurve` component:
 * draws a PP-vs-accuracy chart (with pass/acc/tech breakdown), modifier
 * toggles, an accuracy-range selector and a click-to-highlight point
 * inspector with drag-scrubbable inputs.
 *
 * The curve is driven live from the Acc/Pass/Tech sliders rendered by
 * Leaderboard.cshtml: it reads the slider values and redraws whenever the
 * sliders dispatch a `ratingschange` event.
 */
(function () {
	'use strict';

	if (typeof Chart === 'undefined') {
		console.warn('pp-curve.js: Chart.js is not loaded; PP curve disabled.');
		return;
	}

	// ---------------------------------------------------------------------
	// Constants
	// ---------------------------------------------------------------------
	const GLOBAL_LEADERBOARD_TYPE = 'general';
	const HIGHLIGHT_EPSILON = 0.000001;
	const SCRUB_THRESHOLD_PX = 4;
	const SCRUB_PIXELS_PER_STEP = 14;
	const CHART_CLICKABLE_INSET_PX = 2;

	// Ranked modifier multipliers (portaBLe ModifiersMap.RankedMap()).
	const RANKED_MODIFIERS = {NA: -0.3, NB: -0.2, NO: -0.2, OP: -0.5, FS: 0, SS: 0, SF: 0};
	const MODIFIER_LABELS = {
		FS: 'Faster Song',
		SS: 'Slower Song',
		SF: 'Super Fast Song',
		NA: 'No Arrows',
		NB: 'No Bombs',
		NO: 'No Walls',
		OP: 'NJS cheesing',
	};
	const SPEED_MODIFIERS = ['FS', 'SS', 'SF'];
	const mutuallyExclusive = {
		NA: ['DA'],
		GN: ['DA'],
		DA: ['GN', 'NA'],
		SS: ['FS', 'SF'],
		FS: ['SF', 'SS'],
		SF: ['FS', 'SS'],
	};

	// ---------------------------------------------------------------------
	// Curve math (ported from utils/beatleader/pp.js)
	// ---------------------------------------------------------------------
	const pointList = [
		[1.0, 7.424], [0.999, 6.241], [0.9975, 5.158], [0.995, 4.01], [0.9925, 3.241],
		[0.99, 2.7], [0.9875, 2.303], [0.985, 2.007], [0.9825, 1.786], [0.98, 1.618],
		[0.9775, 1.49], [0.975, 1.392], [0.9725, 1.315], [0.97, 1.256], [0.965, 1.167],
		[0.96, 1.101], [0.955, 1.047], [0.95, 1.0], [0.94, 0.919], [0.93, 0.847],
		[0.92, 0.786], [0.91, 0.734], [0.9, 0.692], [0.875, 0.606], [0.85, 0.537],
		[0.825, 0.48], [0.8, 0.429], [0.75, 0.345], [0.7, 0.286], [0.65, 0.246],
		[0.6, 0.217], [0.0, 0.0],
	];
	const pointList2 = [
		[1.0, 7.424], [0.999, 6.241], [0.9975, 5.158], [0.995, 4.01], [0.9925, 3.241],
		[0.99, 2.7], [0.9875, 2.303], [0.985, 2.007], [0.9825, 1.786], [0.98, 1.618],
		[0.9775, 1.49], [0.975, 1.392], [0.9725, 1.315], [0.97, 1.256], [0.965, 1.167],
		[0.96, 1.094], [0.955, 1.039], [0.95, 1.0], [0.94, 0.931], [0.93, 0.867],
		[0.92, 0.813], [0.91, 0.768], [0.9, 0.729], [0.875, 0.65], [0.85, 0.581],
		[0.825, 0.522], [0.8, 0.473], [0.75, 0.404], [0.7, 0.345], [0.65, 0.296],
		[0.6, 0.256], [0.0, 0.0],
	];

	function interpolateCurve(list, acc) {
		let i = 0;
		for (; i < list.length; i++) {
			if (list[i][0] <= acc) break;
		}
		if (i === 0) i = 1;
		const middleDis = (acc - list[i - 1][0]) / (list[i][0] - list[i - 1][0]);
		return list[i - 1][1] + middleDis * (list[i][1] - list[i - 1][1]);
	}

	const Curve2 = acc => interpolateCurve(pointList2, acc);

	function Inflate(peepee) {
		return (650 * Math.pow(peepee, 1.3)) / Math.pow(650, 1.3);
	}

	function buildCurve(accuracy, passRating, accRating, techRating, golf) {
		let passPP = 15.2 * Math.exp(Math.pow(passRating, 1 / 2.62)) - 30;
		if (!isFinite(passPP) || isNaN(passPP) || passPP < 0) passPP = 0;

		const accPP = golf ? accuracy * accRating * 42 : Curve2(accuracy) * accRating * 34;
		const techPP = Math.exp(1.9 * accuracy) * 1.08 * techRating;
		const totalPp = Inflate(passPP + accPP + techPP);
		const inflation = passPP + accPP + techPP > 0 ? totalPp / (passPP + accPP + techPP) : 0;

		return [totalPp, passPP * inflation, accPP * inflation, techPP * inflation];
	}

	function getPPFromAcc(acc, passRating, accRating, techRating, mode) {
		if (GLOBAL_LEADERBOARD_TYPE === 'golf') {
			return buildCurve(1 - acc, passRating, accRating, techRating, true);
		} else if (mode === 'rhythmgamestandard') {
			return acc * passRating * 55;
		}
		return buildCurve(acc, passRating, accRating, techRating);
	}

	function computeModifiedRating(rating, ratingName, modifiersRating, mods) {
		rating = rating ?? 0;
		if (!mods || !Array.isArray(mods) || mods.length === 0) return rating;

		if (modifiersRating) {
			for (let index = 0; index < mods.length; index++) {
				const mod = mods[index];
				const key = (mod.name ? mod.name.toLowerCase() : '') + ratingName;
				if (modifiersRating[key]) {
					rating = modifiersRating[key];
					mods = mods.filter(m => m !== mod);
					break;
				}
			}
		}

		const positiveSum = mods.reduce((sum, mod) => sum + (mod.value > 0 ? mod.value : 0), 0);
		const negativeSum = mods.reduce((sum, mod) => sum + (mod.value < 0 ? mod.value : 0), 0);
		return rating * (1 + positiveSum + negativeSum);
	}

	function computeStarRating(passRating, accRating, techRating) {
		return Number.isFinite(passRating) && Number.isFinite(accRating) && Number.isFinite(techRating)
			? buildCurve(0.96, passRating, accRating, techRating)[0] / 52
			: null;
	}

	// ---------------------------------------------------------------------
	// Misc helpers
	// ---------------------------------------------------------------------
	function formatNumber(num, digits, withSign) {
		if (num === null || num === undefined || !isFinite(num)) return '-';
		const value = Number(num);
		const text = value.toFixed(digits === null || digits === undefined ? 2 : digits);
		return withSign && value > 0 ? `+${text}` : text;
	}

	function userDescriptionForModifier(modifier) {
		return MODIFIER_LABELS[modifier] ?? 'Unknown modifier';
	}

	function supportsCurveBreakdown(mode) {
		return mode !== 'rhythmgamestandard';
	}

	function getCurveParts(acc, passRating, accRating, techRating, mode) {
		const curve = getPPFromAcc(acc, passRating, accRating, techRating, mode);
		return Array.isArray(curve) ? curve : [curve, null, null, null];
	}

	const roundInputValue = (value, digits = 3) => (Number.isFinite(value) ? Number(value.toFixed(digits)) : 0);

	function toHighlightedInputValue(value, digits = 3) {
		return Number.isFinite(value) ? `${roundInputValue(value, digits)}` : '';
	}

	function getNiceStep(value) {
		if (!Number.isFinite(value) || value <= 0) return 0.1;
		const magnitude = 10 ** Math.floor(Math.log10(value));
		const normalized = value / magnitude;
		const normalizedStep = [1, 2, 2.5, 5, 10].find(candidate => normalized <= candidate) ?? 10;
		return normalizedStep * magnitude;
	}

	function clamp(value, minValue, maxValue) {
		return Math.min(maxValue, Math.max(minValue, value));
	}

	function accToChartX(acc, logarithmic) {
		return logarithmic ? 1 - acc : acc;
	}

	function chartXToAcc(x, logarithmic) {
		return logarithmic ? 1 - x : x;
	}

	function clampHighlightedAcc(acc, startAcc, endAcc) {
		return Math.min(endAcc, Math.max(startAcc, acc));
	}

	const curveDefinitions = [
		{id: 'pp', label: 'PP', color: '#eb008c', visible: () => true, getY: parts => parts[0]},
		{
			id: 'pass',
			label: 'Pass PP',
			color: 'orange',
			visible: (config, mode) => supportsCurveBreakdown(mode) && config && config.ppCurve.passPp,
			getY: parts => parts[1],
		},
		{
			id: 'acc',
			label: 'Acc PP',
			color: 'purple',
			visible: (config, mode) => supportsCurveBreakdown(mode) && config && config.ppCurve.accPp,
			getY: parts => parts[2],
		},
		{
			id: 'tech',
			label: 'Tech PP',
			color: 'red',
			visible: (config, mode) => supportsCurveBreakdown(mode) && config && config.ppCurve.techPp,
			getY: parts => parts[3],
		},
	];

	function getCurveValueRange(graphId, config, passRating, accRating, techRating, mode, startAcc, endAcc) {
		const definition = curveDefinitions.find(curve => curve.id === graphId && curve.visible(config, mode));
		if (!definition) return null;

		const startY = definition.getY(getCurveParts(startAcc, passRating, accRating, techRating, mode));
		const endY = definition.getY(getCurveParts(endAcc, passRating, accRating, techRating, mode));
		if (!Number.isFinite(startY) || !Number.isFinite(endY)) return null;

		return {min: Math.min(startY, endY), max: Math.max(startY, endY)};
	}

	function buildHighlightedGraphs(config, passRating, accRating, techRating, mode, acc) {
		if (!Number.isFinite(acc)) return [];

		const ppParts = getCurveParts(acc, passRating, accRating, techRating, mode);
		return curveDefinitions
			.filter(definition => definition.visible(config, mode))
			.map(definition => ({
				id: definition.id,
				label: definition.label,
				color: definition.color,
				x: acc,
				y: definition.getY(ppParts),
			}))
			.filter(graph => Number.isFinite(graph.y));
	}

	function solveAccForGraphValue(graphId, targetY, config, passRating, accRating, techRating, mode, startAcc, endAcc) {
		const definition = curveDefinitions.find(curve => curve.id === graphId && curve.visible(config, mode));
		if (!definition || !Number.isFinite(targetY)) return null;

		let low = startAcc;
		let high = endAcc;
		const lowY = definition.getY(getCurveParts(low, passRating, accRating, techRating, mode));
		const highY = definition.getY(getCurveParts(high, passRating, accRating, techRating, mode));
		if (!Number.isFinite(lowY) || !Number.isFinite(highY)) return null;

		const ascending = highY >= lowY;
		const minY = Math.min(lowY, highY);
		const maxY = Math.max(lowY, highY);
		const target = Math.min(maxY, Math.max(minY, targetY));

		for (let i = 0; i < 40; i += 1) {
			const mid = (low + high) / 2;
			const midY = definition.getY(getCurveParts(mid, passRating, accRating, techRating, mode));
			if (Math.abs(midY - target) < 0.0001) return mid;
			const goRight = ascending ? midY < target : midY > target;
			if (goRight) low = mid;
			else high = mid;
		}
		return (low + high) / 2;
	}

	function getScrubStep(minValue, maxValue, minimumStep = 0.1) {
		const span = Math.abs(maxValue - minValue);
		if (!Number.isFinite(span) || span <= 0) return minimumStep;
		return Math.max(minimumStep, getNiceStep(span / 240));
	}

	function getChartEventCoordinates(event) {
		const x = event?.x ?? event?.native?.offsetX ?? null;
		const y = event?.y ?? event?.native?.offsetY ?? null;
		if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
		return {x, y};
	}

	function isWithinChartArea(chart, event) {
		const chartArea = chart?.chartArea;
		const coordinates = getChartEventCoordinates(event);
		if (!chartArea || !coordinates) return false;
		return (
			coordinates.x > chartArea.left + CHART_CLICKABLE_INSET_PX &&
			coordinates.x < chartArea.right - CHART_CLICKABLE_INSET_PX &&
			coordinates.y > chartArea.top + CHART_CLICKABLE_INSET_PX &&
			coordinates.y < chartArea.bottom - CHART_CLICKABLE_INSET_PX
		);
	}

	// ---------------------------------------------------------------------
	// Chart.js plugins
	// ---------------------------------------------------------------------
	const regionsPlugin = {
		id: 'regions',
		afterDatasetsDraw(chart, _args, options) {
			const regions = options?.regions ?? [];
			if (!regions.length) return;

			const xScale = chart?.scales?.x;
			if (!xScale) return;

			const {ctx, chartArea} = chart;
			ctx.save();
			regions.forEach(region => {
				const xPixel = xScale.getPixelForValue(region.x);
				if (!Number.isFinite(xPixel)) return;

				ctx.strokeStyle = region.color;
				ctx.globalAlpha = 0.45;
				ctx.lineWidth = 1;
				ctx.beginPath();
				ctx.moveTo(xPixel, chartArea.top);
				ctx.lineTo(xPixel, chartArea.bottom);
				ctx.stroke();
				ctx.globalAlpha = 1;

				if (region.label) {
					ctx.fillStyle = region.color;
					ctx.font = '10px sans-serif';
					ctx.textAlign = 'left';
					ctx.textBaseline = 'top';
					ctx.fillText(region.label, xPixel + 3, chartArea.top + 3);
				}
			});
			ctx.restore();
		},
	};

	const highlightedPointPlugin = {
		id: 'highlightedPoint',
		afterDatasetsDraw(chart, _args, options) {
			const point = options?.point ?? null;
			const graphs = options?.graphs ?? [];
			if (!point || !graphs.length) return;

			const xScale = chart?.scales?.x;
			const yScale = chart?.scales?.y;
			if (!xScale || !yScale) return;

			const xPixel = xScale.getPixelForValue(point.chartX);
			if (!Number.isFinite(xPixel)) return;

			const {ctx, chartArea} = chart;
			ctx.save();
			ctx.strokeStyle = options?.lineColor ?? '#ffffff66';
			ctx.lineWidth = 1;
			ctx.setLineDash([4, 4]);
			ctx.beginPath();
			ctx.moveTo(xPixel, chartArea.top);
			ctx.lineTo(xPixel, chartArea.bottom);
			ctx.stroke();
			ctx.setLineDash([]);

			graphs.forEach(graph => {
				if (!Number.isFinite(graph?.y)) return;
				const yPixel = yScale.getPixelForValue(graph.y);
				if (!Number.isFinite(yPixel)) return;

				ctx.beginPath();
				ctx.fillStyle = graph.color;
				ctx.strokeStyle = options?.outlineColor ?? '#1f1f1f';
				ctx.lineWidth = 2;
				ctx.arc(xPixel, yPixel, 4.5, 0, Math.PI * 2);
				ctx.fill();
				ctx.stroke();
			});
			ctx.restore();
		},
	};

	// ---------------------------------------------------------------------
	// Styles
	// ---------------------------------------------------------------------
	function injectStyles() {
		if (document.getElementById('pp-curve-styles')) return;
		const style = document.createElement('style');
		style.id = 'pp-curve-styles';
		style.textContent = `
		.pp-curve-component {
			width: 100%;
			box-sizing: border-box;
			padding: 1em;
			background-color: #2b2b2b;
			color: #fff;
			font-size: 0.85em;
			min-width: 42em;
		}
		.pp-curve-header {
			display: flex;
			align-items: baseline;
			justify-content: space-between;
			gap: 0.75em;
			margin-bottom: 0.5em;
		}
		.pp-curve-title { font-weight: bold; }
		.pp-curve-stars { color: yellow; font-size: 0.9em; }
		.pp-curve-toolbar {
			display: flex;
			flex-wrap: wrap;
			gap: 0.75em;
			margin-bottom: 0.5em;
		}
		.pp-curve-toolbar label {
			display: flex;
			align-items: center;
			gap: 0.3em;
			cursor: pointer;
			user-select: none;
		}
		.pp-curve-canvas-wrap {
			position: relative;
			height: 220px;
			width: 100%;
		}
		.pp-curve-canvas-wrap canvas { width: 100% !important; }
		.pp-curve-modifiers {
			display: flex;
			flex-wrap: wrap;
			gap: 0.5em;
			justify-content: center;
			margin-top: 0.75em;
		}
		.pp-curve-modifiers button {
			background-color: #4e4e4e;
			color: #fff;
			border: none;
			border-radius: 0.3em;
			padding: 0.2em 0.5em;
			min-width: 2.8em;
			cursor: pointer;
			transition: background-color 200ms, color 200ms;
		}
		.pp-curve-modifiers button.selected { background-color: #838383; }
		.pp-curve-modifiers button.disabled {
			color: #888;
			background-color: #212121;
			cursor: default;
		}
		.pp-curve-range {
			margin-top: 0.75em;
			display: flex;
			flex-direction: column;
			gap: 0.25em;
		}
		.pp-curve-range-label {
			display: flex;
			justify-content: space-between;
			font-size: 0.8em;
		}
		.pp-curve-range input[type="range"] { width: 100%; }
		.pp-curve-panel {
			margin-top: 0.75em;
			padding: 0.75em;
			border-radius: 0.4em;
			background: #1f1f1f;
			display: flex;
			flex-direction: column;
			gap: 0.6em;
		}
		.pp-curve-panel-header {
			display: flex;
			align-items: center;
			justify-content: space-between;
			gap: 0.75em;
		}
		.pp-curve-panel-title { font-weight: bold; }
		.pp-curve-panel-clear {
			border: 0;
			border-radius: 0.35em;
			background: #4e4e4e;
			color: #fff;
			padding: 0.25em 0.6em;
			cursor: pointer;
		}
		.pp-curve-panel-grid {
			display: flex;
			flex-direction: column;
			gap: 0.4em;
		}
		.pp-curve-panel-row {
			display: grid;
			grid-template-columns: minmax(0, 1.3fr) minmax(0, 0.85fr) minmax(0, 1fr);
			gap: 0.5em;
			align-items: center;
		}
		.pp-curve-panel-row.head {
			color: #9a9a9a;
			font-size: 0.75em;
			text-transform: uppercase;
			letter-spacing: 0.04em;
		}
		.pp-curve-legend {
			display: flex;
			align-items: center;
			gap: 0.4em;
			min-width: 0;
		}
		.pp-curve-swatch {
			width: 0.7em;
			height: 0.7em;
			border-radius: 999px;
			flex: none;
		}
		.pp-curve-panel-row input {
			width: 100%;
			box-sizing: border-box;
			border: 0;
			border-radius: 0.35em;
			background: #2f2f2f;
			color: #fff;
			padding: 0.35em 0.5em;
			cursor: ew-resize;
			-moz-appearance: textfield;
		}
		.pp-curve-panel-row input:focus { cursor: text; }
		.pp-curve-panel-row input::-webkit-outer-spin-button,
		.pp-curve-panel-row input::-webkit-inner-spin-button {
			-webkit-appearance: none;
			margin: 0;
		}
		@media screen and (max-width: 640px) {
			.pp-curve-panel-row,
			.pp-curve-panel-row.head { grid-template-columns: 1fr; }
			.pp-curve-panel-row.head { display: none; }
		}
		`;
		document.head.appendChild(style);
	}

	// ---------------------------------------------------------------------
	// Component
	// ---------------------------------------------------------------------
	function PpCurve(root) {
		const panel = root.closest('.leaderboard-container');
		const sliders = panel ? panel.querySelector('.rating-sliders') : null;

		const mode = (root.dataset.mode || '').toLowerCase();
		let modifiersRating = null;
		try {
			const raw = root.dataset.modifiersRating;
			if (raw) modifiersRating = JSON.parse(raw);
		} catch (e) {
			modifiersRating = null;
		}

		const gridColor = '#3a3a3a';
		const mainColor = '#eb008c';
		const annotationColor = '#aaa';
		const textColor = '#fff';

		// State
		const config = {ppCurve: {passPp: true, accPp: true, techPp: true}};
		let logarithmic = false;
		let startAcc = 0.6;
		let endAcc = 1.0;
		const ABS_MIN = 0.5;
		const ABS_MAX = 1.0;
		let selectedModifiers = [];
		let highlightedPoint = null;
		let highlightedAccInput = '';
		let highlightedGraphInputs = {};
		let activeHighlightedField = null;
		let highlightedScrub = null;
		let chart = null;
		let panelGraphKey = null;
		const accInputEls = [];
		const graphInputEls = {};

		// Derived (recomputed every redraw)
		let modifiedPassRating = 0;
		let modifiedAccRating = 0;
		let modifiedTechRating = 0;
		let highlightedGraphs = [];

		// --- DOM ----------------------------------------------------------
		const availableModifiers = Object.keys(RANKED_MODIFIERS)
			.filter(name => (SPEED_MODIFIERS.includes(name) ? !!modifiersRating : true))
			.map(name => ({name, value: RANKED_MODIFIERS[name]}))
			.sort((a, b) => b.value - a.value);

		const headerEl = el('div', 'pp-curve-header');
		const titleEl = el('span', 'pp-curve-title', 'PP Curve');
		const starsEl = el('span', 'pp-curve-stars');
		headerEl.append(titleEl, starsEl);

		const toolbarEl = el('div', 'pp-curve-toolbar');
		const passToggle = checkbox('Pass PP', true, value => {
			config.ppCurve.passPp = value;
			panelGraphKey = null;
			redraw();
		});
		const accToggle = checkbox('Acc PP', true, value => {
			config.ppCurve.accPp = value;
			panelGraphKey = null;
			redraw();
		});
		const techToggle = checkbox('Tech PP', true, value => {
			config.ppCurve.techPp = value;
			panelGraphKey = null;
			redraw();
		});
		const logToggle = checkbox('Logarithmic', false, value => {
			logarithmic = value;
			redraw();
		});
		toolbarEl.append(passToggle.label, accToggle.label, techToggle.label, logToggle.label);

		const canvasWrap = el('section', 'pp-curve-canvas-wrap');
		const canvas = document.createElement('canvas');
		canvasWrap.appendChild(canvas);

		const modifiersEl = el('div', 'pp-curve-modifiers');

		const rangeEl = el('div', 'pp-curve-range');
		const rangeLabel = el('div', 'pp-curve-range-label');
		const rangeText = el('span', '', 'Accuracy range');
		const rangeValue = el('span');
		rangeLabel.append(rangeText, rangeValue);
		const startRange = rangeInput(ABS_MIN, ABS_MAX, 0.001, startAcc);
		const endRange = rangeInput(ABS_MIN, ABS_MAX, 0.001, endAcc);
		startRange.addEventListener('input', () => {
			startAcc = Math.min(parseFloat(startRange.value), endAcc - 0.01);
			startRange.value = startAcc;
			redraw();
		});
		endRange.addEventListener('input', () => {
			endAcc = Math.max(parseFloat(endRange.value), startAcc + 0.01);
			endRange.value = endAcc;
			redraw();
		});
		rangeEl.append(rangeLabel, startRange, endRange);

		const panelEl = el('div', 'pp-curve-panel');
		panelEl.style.display = 'none';

		root.append(headerEl, toolbarEl, canvasWrap, modifiersEl, rangeEl, panelEl);

		// --- Modifiers ----------------------------------------------------
		function renderModifiers() {
			const excluded = selectedModifiers.reduce(
				(all, mod) => (mutuallyExclusive[mod.name] ? all.concat(mutuallyExclusive[mod.name]) : all),
				[]
			);
			modifiersEl.innerHTML = '';
			availableModifiers.forEach(modifier => {
				const isSelected = selectedModifiers.some(m => m.name === modifier.name);
				const isDisabled = excluded.includes(modifier.name) && !isSelected;
				const button = el('button', '', modifier.name);
				button.type = 'button';
				if (isSelected) button.classList.add('selected');
				if (isDisabled) button.classList.add('disabled');
				button.title = `${userDescriptionForModifier(modifier.name)}: ${formatNumber(modifier.value * 100, 0, true)}%`;
				button.addEventListener('click', () => {
					if (isDisabled) return;
					selectedModifiers = isSelected
						? selectedModifiers.filter(m => m.name !== modifier.name)
						: [...selectedModifiers, modifier];
					redraw();
				});
				modifiersEl.appendChild(button);
			});
		}

		// --- Ratings ------------------------------------------------------
		function readSlider(rating, fallback) {
			if (!sliders) return fallback;
			const input = sliders.querySelector(`input[type="number"][data-rating="${rating}"]`);
			const value = input ? parseFloat(input.value) : NaN;
			return Number.isFinite(value) ? value : fallback;
		}

		function baseRatings() {
			return {
				pass: readSlider('pass', 5),
				acc: readSlider('acc', 5),
				tech: readSlider('tech', 5),
			};
		}

		// --- Highlighted point --------------------------------------------
		function updateHighlightedPoint(acc) {
			if (!Number.isFinite(acc)) return;
			const clampedAcc = clampHighlightedAcc(acc, startAcc, endAcc);
			highlightedPoint = {acc: clampedAcc, chartX: accToChartX(clampedAcc, logarithmic)};
			redraw();
		}

		function clearHighlightedPoint() {
			highlightedPoint = null;
			activeHighlightedField = null;
			redraw();
		}

		function handleChartHover(event) {
			const target = chart?.canvas ?? event?.native?.target;
			if (!target?.style) return;
			target.style.cursor = isWithinChartArea(chart, event) ? 'pointer' : 'default';
		}

		function handleChartClick(event) {
			const xScale = chart?.scales?.x;
			const coordinates = getChartEventCoordinates(event);
			if (!xScale || !coordinates || !isWithinChartArea(chart, event)) return;

			const chartX = xScale.getValueForPixel(coordinates.x);
			if (!Number.isFinite(chartX)) return;
			updateHighlightedPoint(chartXToAcc(chartX, logarithmic));
		}

		function isActiveHighlightedField(field) {
			if (!activeHighlightedField || !field) return false;
			if (activeHighlightedField.type !== field.type) return false;
			if (field.type !== 'graph') return true;
			return activeHighlightedField.graphId === field.graphId;
		}

		function setActiveHighlightedField(field) {
			activeHighlightedField = field;
		}

		function clearActiveHighlightedField(field) {
			if (!field || isActiveHighlightedField(field)) activeHighlightedField = null;
		}

		function syncHighlightedInputs(point, graphs) {
			if (!point) {
				highlightedAccInput = '';
				highlightedGraphInputs = {};
				return;
			}
			if (!isActiveHighlightedField({type: 'acc'})) {
				highlightedAccInput = toHighlightedInputValue(point.acc * 100, 3);
			}
			highlightedGraphInputs = graphs.reduce((all, graph) => {
				all[graph.id] =
					isActiveHighlightedField({type: 'graph', graphId: graph.id}) && highlightedGraphInputs?.[graph.id] !== undefined
						? highlightedGraphInputs[graph.id]
						: toHighlightedInputValue(graph.y, 3);
				return all;
			}, {});
		}

		function previewHighlightedAccInput(value) {
			const nextAcc = Number(value);
			const minVisibleAcc = startAcc * 100;
			const maxVisibleAcc = endAcc * 100;
			if (!Number.isFinite(nextAcc) || nextAcc < minVisibleAcc - HIGHLIGHT_EPSILON || nextAcc > maxVisibleAcc + HIGHLIGHT_EPSILON) {
				return false;
			}
			updateHighlightedPoint(nextAcc / 100);
			return true;
		}

		function previewHighlightedGraphInput(graphId, value) {
			const targetY = Number(value);
			const visibleRange = getCurveValueRange(
				graphId, config, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode, startAcc, endAcc
			);
			if (
				!visibleRange ||
				!Number.isFinite(targetY) ||
				targetY < visibleRange.min - HIGHLIGHT_EPSILON ||
				targetY > visibleRange.max + HIGHLIGHT_EPSILON
			) {
				return false;
			}
			const nextAcc = solveAccForGraphValue(
				graphId, targetY, config, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode, startAcc, endAcc
			);
			if (!Number.isFinite(nextAcc)) return false;
			updateHighlightedPoint(nextAcc);
			return true;
		}

		function commitHighlightedAcc() {
			if (!highlightedPoint) return;
			const nextAcc = Number(highlightedAccInput);
			if (!Number.isFinite(nextAcc)) {
				redraw();
				return;
			}
			updateHighlightedPoint(nextAcc / 100);
		}

		function commitHighlightedGraph(graphId) {
			if (!highlightedPoint) return;
			const targetY = Number(highlightedGraphInputs?.[graphId]);
			if (!Number.isFinite(targetY)) {
				redraw();
				return;
			}
			const nextAcc = solveAccForGraphValue(
				graphId, targetY, config, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode, startAcc, endAcc
			);
			if (!Number.isFinite(nextAcc)) {
				redraw();
				return;
			}
			updateHighlightedPoint(nextAcc);
		}

		function handleHighlightedInputKeydown(event, commit) {
			if (event.key !== 'Enter') return;
			event.preventDefault();
			commit();
			event.currentTarget.blur();
		}

		// --- Scrub --------------------------------------------------------
		function stopHighlightedScrub(event) {
			if (!highlightedScrub) return;
			if (event?.pointerId !== undefined && event.pointerId !== highlightedScrub.pointerId) return;

			try {
				highlightedScrub.element?.releasePointerCapture?.(highlightedScrub.pointerId);
			} catch (e) {
				/* ignore */
			}
			window.removeEventListener('pointermove', handleHighlightedScrubMove);
			window.removeEventListener('pointerup', stopHighlightedScrub);
			window.removeEventListener('pointercancel', stopHighlightedScrub);

			if (document?.body) {
				document.body.style.cursor = highlightedScrub.previousCursor;
				document.body.style.userSelect = highlightedScrub.previousUserSelect;
			}
			highlightedScrub = null;
		}

		function handleHighlightedScrubMove(event) {
			if (!highlightedScrub || event.pointerId !== highlightedScrub.pointerId) return;

			const deltaX = event.clientX - highlightedScrub.startX;
			if (!highlightedScrub.started && Math.abs(deltaX) < SCRUB_THRESHOLD_PX) return;

			if (!highlightedScrub.started) {
				highlightedScrub.started = true;
				highlightedScrub.element?.blur?.();
				if (document?.body) {
					document.body.style.cursor = 'ew-resize';
					document.body.style.userSelect = 'none';
				}
			}

			event.preventDefault();
			const nextValue = clamp(
				highlightedScrub.initialValue + (deltaX / SCRUB_PIXELS_PER_STEP) * highlightedScrub.step,
				highlightedScrub.minValue,
				highlightedScrub.maxValue
			);
			const roundedValue = roundInputValue(nextValue, highlightedScrub.precision);
			if (Math.abs(roundedValue - highlightedScrub.lastValue) <= HIGHLIGHT_EPSILON) return;

			highlightedScrub.lastValue = roundedValue;
			highlightedScrub.onUpdate(roundedValue);
		}

		function startHighlightedScrub(event, options) {
			if (!highlightedPoint || event.button !== 0 || event.pointerType === 'touch' || event.pointerType === 'pen') return;

			stopHighlightedScrub();
			highlightedScrub = {
				pointerId: event.pointerId,
				element: event.currentTarget,
				startX: event.clientX,
				initialValue: options.initialValue,
				lastValue: options.initialValue,
				minValue: options.minValue,
				maxValue: options.maxValue,
				step: options.step,
				precision: options.precision ?? 3,
				onUpdate: options.onUpdate,
				started: false,
				previousCursor: document?.body?.style.cursor ?? '',
				previousUserSelect: document?.body?.style.userSelect ?? '',
			};

			try {
				event.currentTarget?.setPointerCapture?.(event.pointerId);
			} catch (e) {
				/* ignore */
			}
			window.addEventListener('pointermove', handleHighlightedScrubMove);
			window.addEventListener('pointerup', stopHighlightedScrub);
			window.addEventListener('pointercancel', stopHighlightedScrub);
		}

		function startHighlightedAccScrub(event) {
			setActiveHighlightedField({type: 'acc'});
			startHighlightedScrub(event, {
				initialValue: Number(highlightedAccInput || (highlightedPoint?.acc ?? 0) * 100 || 0),
				minValue: startAcc * 100,
				maxValue: endAcc * 100,
				step: 0.1,
				precision: 3,
				onUpdate: value => {
					highlightedAccInput = toHighlightedInputValue(value, 3);
					previewHighlightedAccInput(value);
				},
			});
		}

		function startHighlightedGraphScrub(event, graphId) {
			const graph = highlightedGraphs.find(entry => entry.id === graphId);
			const visibleRange = getCurveValueRange(
				graphId, config, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode, startAcc, endAcc
			);
			if (!graph || !visibleRange) return;

			setActiveHighlightedField({type: 'graph', graphId});
			startHighlightedScrub(event, {
				initialValue: Number(highlightedGraphInputs?.[graphId] || graph.y || 0),
				minValue: visibleRange.min,
				maxValue: visibleRange.max,
				step: getScrubStep(visibleRange.min, visibleRange.max),
				precision: 3,
				onUpdate: value => {
					highlightedGraphInputs = {...highlightedGraphInputs, [graphId]: toHighlightedInputValue(value, 3)};
					previewHighlightedGraphInput(graphId, value);
				},
			});
		}

		// --- Highlight panel ----------------------------------------------
		function buildPanelRows(graphs) {
			panelEl.innerHTML = '';
			accInputEls.length = 0;
			for (const key in graphInputEls) delete graphInputEls[key];

			const header = el('div', 'pp-curve-panel-header');
			header.append(el('span', 'pp-curve-panel-title', 'Highlighted Point'));
			const clearButton = el('button', 'pp-curve-panel-clear', 'Clear');
			clearButton.type = 'button';
			clearButton.addEventListener('click', clearHighlightedPoint);
			header.append(clearButton);

			const grid = el('div', 'pp-curve-panel-grid');
			const headRow = el('div', 'pp-curve-panel-row head');
			headRow.append(el('span', '', 'Curve'), el('span', '', 'Acc %'), el('span', '', 'PP'));
			grid.appendChild(headRow);

			graphs.forEach(graph => {
				const row = el('div', 'pp-curve-panel-row');

				const legend = el('div', 'pp-curve-legend');
				const swatch = el('span', 'pp-curve-swatch');
				swatch.style.background = graph.color;
				legend.append(swatch, el('span', '', graph.label));

				const accInput = numberInput('0.1');
				accInput.addEventListener('pointerdown', startHighlightedAccScrub);
				accInput.addEventListener('focus', () => setActiveHighlightedField({type: 'acc'}));
				accInput.addEventListener('input', () => {
					highlightedAccInput = accInput.value;
					previewHighlightedAccInput(highlightedAccInput);
				});
				accInput.addEventListener('keydown', event => handleHighlightedInputKeydown(event, commitHighlightedAcc));
				accInput.addEventListener('change', commitHighlightedAcc);
				accInput.addEventListener('blur', () => {
					clearActiveHighlightedField({type: 'acc'});
					commitHighlightedAcc();
				});
				accInputEls.push(accInput);

				const graphInput = numberInput('any');
				graphInput.addEventListener('pointerdown', event => startHighlightedGraphScrub(event, graph.id));
				graphInput.addEventListener('focus', () => setActiveHighlightedField({type: 'graph', graphId: graph.id}));
				graphInput.addEventListener('input', () => {
					highlightedGraphInputs = {...highlightedGraphInputs, [graph.id]: graphInput.value};
					previewHighlightedGraphInput(graph.id, graphInput.value);
				});
				graphInput.addEventListener('keydown', event =>
					handleHighlightedInputKeydown(event, () => commitHighlightedGraph(graph.id))
				);
				graphInput.addEventListener('change', () => commitHighlightedGraph(graph.id));
				graphInput.addEventListener('blur', () => {
					clearActiveHighlightedField({type: 'graph', graphId: graph.id});
					commitHighlightedGraph(graph.id);
				});
				graphInputEls[graph.id] = graphInput;

				row.append(legend, accInput, graphInput);
				grid.appendChild(row);
			});

			panelEl.append(header, grid);
		}

		function applyInputValues() {
			const activeEl = highlightedScrub?.element ?? document.activeElement;
			accInputEls.forEach(input => {
				if (input !== activeEl) input.value = highlightedAccInput;
			});
			Object.keys(graphInputEls).forEach(id => {
				const input = graphInputEls[id];
				if (input !== activeEl) input.value = highlightedGraphInputs?.[id] ?? '';
			});
		}

		function renderPanel() {
			if (!highlightedPoint) {
				panelEl.style.display = 'none';
				panelEl.innerHTML = '';
				panelGraphKey = null;
				return;
			}
			const key = highlightedGraphs.map(graph => graph.id).join(',');
			if (key !== panelGraphKey) {
				buildPanelRows(highlightedGraphs);
				panelGraphKey = key;
			}
			panelEl.style.display = '';
			syncHighlightedInputs(highlightedPoint, highlightedGraphs);
			applyInputValues();
		}

		// --- Chart --------------------------------------------------------
		function setupChart() {
			const totalPPData = [];
			const passPPData = [];
			const accPPData = [];
			const techPPData = [];
			const annotations = [];

			const step = Math.max(0.0002, (endAcc - startAcc) / 1400);
			for (let acc = startAcc; acc < endAcc; acc += step) {
				const ppParts = getCurveParts(acc, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode);
				const chartX = accToChartX(acc, logarithmic);
				totalPPData.push({x: chartX, y: ppParts[0]});
				passPPData.push({x: chartX, y: ppParts[1]});
				accPPData.push({x: chartX, y: ppParts[2]});
				techPPData.push({x: chartX, y: ppParts[3]});
			}

			const annStep = Math.max(1, Math.round(((endAcc - startAcc) * 100) / 8));
			for (let percent = Math.ceil((startAcc * 100) / annStep) * annStep; percent < endAcc * 100; percent += annStep) {
				const acc = percent / 100;
				if (acc <= startAcc) continue;
				const pp = getCurveParts(acc, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode)[0];
				if (pp) {
					annotations.push({
						x: accToChartX(acc, logarithmic),
						color: annotationColor,
						label: `${formatNumber(pp, 0)}pp`,
					});
				}
			}

			const datasets = [
				{data: totalPPData, borderColor: mainColor, borderWidth: 2, pointRadius: 0, tension: 0.4, type: 'line', label: 'PP'},
			];
			if (supportsCurveBreakdown(mode) && config.ppCurve.passPp) {
				datasets.push({data: passPPData, borderColor: 'orange', borderWidth: 2, pointRadius: 0, tension: 0.4, type: 'line', label: 'Pass PP'});
			}
			if (supportsCurveBreakdown(mode) && config.ppCurve.accPp) {
				datasets.push({data: accPPData, borderColor: 'purple', borderWidth: 2, pointRadius: 0, tension: 0.4, type: 'line', label: 'Acc PP'});
			}
			if (supportsCurveBreakdown(mode) && config.ppCurve.techPp) {
				datasets.push({data: techPPData, borderColor: 'red', borderWidth: 2, pointRadius: 0, tension: 0.4, type: 'line', label: 'Tech PP'});
			}

			const xAxis = {
				type: logarithmic ? 'logarithmic' : 'linear',
				reverse: logarithmic,
				display: true,
				ticks: {
					color: textColor,
					autoSkip: true,
					callback: val => {
						const pct = (logarithmic ? 1 - val : val) * 100;
						return Math.abs(pct - Math.round(pct)) < 1e-6 ? `${formatNumber(Math.round(pct), 0)}%` : null;
					},
				},
				grid: {color: gridColor},
				min: startAcc,
				max: endAcc,
			};
			const yAxis = {
				display: true,
				position: 'left',
				title: {display: true, text: 'pp', color: textColor},
				ticks: {color: textColor, callback: val => (val === Math.floor(val) ? val : null), precision: 0},
				grid: {color: gridColor},
			};

			const highlightedPointOptions = {
				point: highlightedPoint,
				graphs: highlightedGraphs,
				lineColor: annotationColor,
				outlineColor: '#1f1f1f',
			};

			if (!chart) {
				chart = new Chart(canvas, {
					type: 'line',
					data: {datasets},
					options: {
						responsive: true,
						animation: {duration: 0},
						maintainAspectRatio: false,
						color: textColor,
						layout: {padding: {right: 0}},
						interaction: {mode: 'index', intersect: false},
						plugins: {
							legend: {display: false},
							tooltip: {
								position: 'nearest',
								callbacks: {
									title(ctx) {
										if (!ctx?.[0]?.raw) return '';
										const accuracy = Math.round(ctx[0].raw.x * 100000) / 1000;
										return `acc: ${formatNumber(logarithmic ? 100 - accuracy : accuracy, 3)}%`;
									},
									label(ctx) {
										return `${formatNumber(ctx.parsed.y, ctx.dataset.round)} ${ctx.dataset.label}`;
									},
								},
							},
							regions: {regions: annotations},
							highlightedPoint: highlightedPointOptions,
						},
						onClick: event => handleChartClick(event),
						onHover: event => handleChartHover(event),
						scales: {x: xAxis, y: yAxis},
					},
					plugins: [regionsPlugin, highlightedPointPlugin],
				});
			} else {
				chart.data = {datasets};
				chart.options.scales = {x: xAxis, y: yAxis};
				chart.options.plugins.regions = {regions: annotations};
				chart.options.plugins.highlightedPoint = highlightedPointOptions;
				chart.options.onClick = event => handleChartClick(event);
				chart.options.onHover = event => handleChartHover(event);
				chart.update();
			}
		}

		// --- Redraw -------------------------------------------------------
		function redraw() {
			const base = baseRatings();
			modifiedPassRating = computeModifiedRating(base.pass, 'PassRating', modifiersRating, selectedModifiers);
			modifiedAccRating = computeModifiedRating(base.acc, 'AccRating', modifiersRating, selectedModifiers);
			modifiedTechRating = computeModifiedRating(base.tech, 'TechRating', modifiersRating, selectedModifiers);

			if (highlightedPoint) {
				const clampedAcc = clampHighlightedAcc(highlightedPoint.acc, startAcc, endAcc);
				highlightedPoint = {acc: clampedAcc, chartX: accToChartX(clampedAcc, logarithmic)};
				highlightedGraphs = buildHighlightedGraphs(
					config, modifiedPassRating, modifiedAccRating, modifiedTechRating, mode, highlightedPoint.acc
				);
			} else {
				highlightedGraphs = [];
			}

			const modifiedStars =
				selectedModifiers.length &&
				(base.pass !== modifiedPassRating || base.acc !== modifiedAccRating || base.tech !== modifiedTechRating)
					? computeStarRating(modifiedPassRating, modifiedAccRating, modifiedTechRating)
					: null;
			starsEl.textContent = modifiedStars != null ? `With modifiers: ${formatNumber(modifiedStars, 2)} ★` : '';

			rangeValue.textContent = `${formatNumber(startAcc * 100, 0)}% – ${formatNumber(endAcc * 100, 0)}%`;

			renderModifiers();
			setupChart();
			renderPanel();
		}

		// --- Wire up ------------------------------------------------------
		if (sliders) sliders.addEventListener('ratingschange', redraw);
		redraw();
	}

	// ---------------------------------------------------------------------
	// Small DOM helpers
	// ---------------------------------------------------------------------
	function el(tag, className, text) {
		const node = document.createElement(tag);
		if (className) node.className = className;
		if (text !== undefined) node.textContent = text;
		return node;
	}

	function checkbox(labelText, checked, onChange) {
		const label = document.createElement('label');
		const input = document.createElement('input');
		input.type = 'checkbox';
		input.checked = checked;
		input.addEventListener('change', () => onChange(input.checked));
		label.append(input, document.createTextNode(labelText));
		return {label, input};
	}

	function rangeInput(min, max, step, value) {
		const input = document.createElement('input');
		input.type = 'range';
		input.min = min;
		input.max = max;
		input.step = step;
		input.value = value;
		return input;
	}

	function numberInput(step) {
		const input = document.createElement('input');
		input.type = 'number';
		input.step = step;
		return input;
	}

	// ---------------------------------------------------------------------
	// Init
	// ---------------------------------------------------------------------
	function initAll() {
		injectStyles();
		document.querySelectorAll('.pp-curve-component').forEach(node => {
			if (node.dataset.ppCurveInit) return;
			node.dataset.ppCurveInit = '1';
			try {
				PpCurve(node);
			} catch (error) {
				console.error('pp-curve.js: failed to initialize', error);
			}
		});
	}

	if (document.readyState === 'loading') {
		document.addEventListener('DOMContentLoaded', initAll);
	} else {
		initAll();
	}
})();
