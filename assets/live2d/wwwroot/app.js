/* ============================================================
 * VoxIME Live2D 桌宠页面（原生 JS，无构建）
 *
 * 职责：
 *  - 加载 Cubism 3/4 模型（.model3.json），居中自适应缩放、透明背景；
 *  - 自动待机动作（优先 Idle 动作组，缺失时退化为任意动作组）；
 *  - 眨眼由 Cubism EyeBlink 自动更新（model3.json 里 Groups 声明）；
 *  - 口型：setMouth 数值写入 LipSync 参数（Hiyori 为 ParamMouthOpenY）；
 *  - 眼动：setGaze 归一化坐标驱动 ParamAngleX/Y + ParamEyeBallX/Y（focusController）；
 *  - WPF ↔ JS 消息桥：chrome.webview.postMessage / addEventListener('message')；
 *  - 无模型 / 加载失败时的 Canvas 兜底形象（独立预览页与无 WebView2 环境可用）。
 *
 * 宿主消息（C# → JS）：
 *   {type:"loadModel", url} / {type:"setState", state} / {type:"setMouth", value}
 *   {type:"setGaze", x, y} / {type:"setBubble", text} / {type:"setConfig", eyeFollow, idleMotion, mouthSync, scale, ...}
 * 页面消息（JS → C#）：
 *   {type:"ready"} / {type:"modelLoaded", name, width, height} / {type:"error", message}
 * ============================================================ */
(function () {
  'use strict';

  var VERSION = '1.0.0';
  var params = new URLSearchParams(location.search);
  var page = (document.body && document.body.dataset && document.body.dataset.page) || 'pet';
  var isPreview = page === 'preview';
  var embedded = page === 'pet' && params.get('embedded') === '1';
  var forceDemo = params.get('demo') === '1';
  var showDebug = isPreview || params.get('status') === '1';

  var stageEl = document.getElementById('stage');
  var demoCanvas = document.getElementById('demo-canvas');
  var bubbleEl = document.getElementById('bubble');
  var hintEl = document.getElementById('hint');
  var statusEl = document.getElementById('status');

  // ---------------- 运行时配置与状态 ----------------
  var config = {
    eyeFollow: true,
    idleMotion: true,
    mouthSync: true,
    scale: 1.0,
    idleMotionGroup: 'Idle',
    showBubble: true,
    bubbleInPage: isPreview // 嵌入时气泡由 WPF 层渲染，避免重复
  };

  var live = {
    state: 'idle',
    mouth: 0,          // 当前平滑值
    targetMouth: 0,    // 目标值
    gazeX: 0,          // 当前平滑视线（渲染用）
    gazeY: 0,
    targetGazeX: 0,    // C# 采样下发的目标视线
    targetGazeY: 0
  };

  var stateExpressions = { listening: null, thinking: null, speaking: null, error: null };

  var pixiApp = null;
  var model = null;
  var modelUrl = null;
  var modelName = '';
  var modelGroups = [];
  var idleGroup = null;
  var mouthParamIds = ['ParamMouthOpenY'];
  var demo = null;
  var demoActive = false;
  var readySent = false;
  var lastStatusAt = 0;
  var fpsCounter = { frames: 0, since: performance.now(), value: 0 };
  var diag = { modelUpdateTicks: 0, mouthWrites: 0, mouthErrors: 0, lastMouthError: '' };

  // ---------------- 基础工具 ----------------
  function log() {
    var args = Array.prototype.slice.call(arguments);
    args.unshift('[VoxIME/Live2D]');
    try { console.log.apply(console, args); } catch (e) { /* ignore */ }
  }

  function hasHost() {
    return !!(window.chrome && window.chrome.webview && window.chrome.webview.postMessage);
  }

  function sendHost(message) {
    if (!hasHost()) return;
    try { window.chrome.webview.postMessage(message); } catch (e) { log('postMessage 失败', e); }
  }

  function sendReady() {
    if (readySent) return;
    readySent = true;
    sendHost({ type: 'ready', version: VERSION, page: page, webgl: hasWebGL() });
    log('已发送 ready');
  }

  function hasWebGL() {
    try {
      var c = document.createElement('canvas');
      return !!(c.getContext('webgl') || c.getContext('experimental-webgl'));
    } catch (e) { return false; }
  }

  function setHint(text, isError) {
    if (!hintEl) return;
    if (!text) { hintEl.classList.add('hidden'); return; }
    hintEl.textContent = text;
    hintEl.classList.remove('hidden');
    hintEl.classList.toggle('error', !!isError);
  }

  function setStatus(text) {
    if (!statusEl) return;
    statusEl.innerHTML = text;
  }

  function escapeHtml(text) {
    return String(text == null ? '' : text)
      .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
  }

  function fmt(value, digits) {
    if (typeof value !== 'number' || !isFinite(value)) return '-';
    return value.toFixed(digits == null ? 2 : digits);
  }

  function clamp01(v) {
    return Math.min(1, Math.max(0, Number(v) || 0));
  }

  function clampGaze(v) {
    var n = Number(v);
    if (!isFinite(n)) return 0;
    return Math.min(1, Math.max(-1, n));
  }

  function normalizeState(name) {
    var s = String(name || '').toLowerCase();
    if (s === 'listening' || s === 'listen') return 'listening';
    if (s === 'thinking' || s === 'think') return 'thinking';
    if (s === 'speaking' || s === 'speak') return 'speaking';
    if (s === 'error' || s === 'failed') return 'error';
    return 'idle';
  }

  // ---------------- 画布与布局 ----------------
  function ensurePixi() {
    if (pixiApp) return pixiApp;
    var canvas = document.getElementById('live2d-canvas');
    if (!canvas) {
      canvas = document.createElement('canvas');
      canvas.id = 'live2d-canvas';
      canvas.className = 'hidden';
      // 放在 demo canvas 之后、气泡之前
      stageEl.insertBefore(canvas, bubbleEl);
    }
    pixiApp = new PIXI.Application({
      view: canvas,
      width: Math.max(1, stageEl.clientWidth),
      height: Math.max(1, stageEl.clientHeight),
      backgroundAlpha: 0,
      antialias: true,
      autoDensity: true,
      resolution: Math.min(window.devicePixelRatio || 1, 2),
      powerPreference: 'high-performance'
    });
    pixiApp.ticker.add(onTick);
    return pixiApp;
  }

  function applyRendererResolution() {
    if (!pixiApp) return;
    // 放大模型时提高画布后备分辨率，避免把画面“拉大”后发糊；
    // 缩放只在 canvas 内完成，不再改 WebView2 ZoomFactor（否则 viewport 重排会让缩放几乎无效）。
    var base = Math.min(window.devicePixelRatio || 1, 2);
    var factor = Math.max(1, config.scale || 1);
    var resolution = Math.min(3, base * factor);
    if (Math.abs((pixiApp.renderer.resolution || 1) - resolution) > 0.01) {
      pixiApp.renderer.resolution = resolution;
    }
  }

  function resizePixi() {
    if (!pixiApp) return;
    var w = Math.max(1, stageEl.clientWidth);
    var h = Math.max(1, stageEl.clientHeight);
    applyRendererResolution();
    try { pixiApp.renderer.resize(w, h); } catch (e) { log('resize 失败', e); }
    layoutModel();
  }

  function layoutModel() {
    if (!model) return;
    var w = stageEl.clientWidth;
    var h = stageEl.clientHeight;
    if (!w || !h) return;
    var modelW = model.internalModel.width || 1;
    var modelH = model.internalModel.height || 1;
    var fill = 0.95;
    var scale = Math.min(w / modelW, h / modelH) * fill * (config.scale || 1);
    model.anchor.set(0.5, 1);
    model.scale.set(scale);
    model.position.set(w / 2, h - Math.min(14, h * 0.02));
    log('布局: 画布 ' + w + 'x' + h + ', 模型 ' + fmt(modelW, 0) + 'x' + fmt(modelH, 0) +
      ', scale=' + fmt(scale, 3));
  }

  function onTick() {
    var now = performance.now();
    var dt = Math.min(0.1, pixiApp.ticker.deltaMS / 1000);
    fpsCounter.frames++;
    if (now - fpsCounter.since >= 1000) {
      fpsCounter.value = Math.round(fpsCounter.frames * 1000 / (now - fpsCounter.since));
      fpsCounter.frames = 0;
      fpsCounter.since = now;
    }

    // 口型/眼动平滑（C# 以配置帧率推送目标值，页面侧插值让动作自然，避免高帧率下出现抖动）
    var k = Math.min(1, dt * 16);
    live.mouth += (live.targetMouth - live.mouth) * k;
    var gazeK = Math.min(1, dt * 25);
    live.gazeX += (live.targetGazeX - live.gazeX) * gazeK;
    live.gazeY += (live.targetGazeY - live.gazeY) * gazeK;

    if (model) {
      if (config.eyeFollow) {
        try { model.internalModel.focusController.focus(live.gazeX, live.gazeY); } catch (e) { /* 模型不支持眼动时忽略 */ }
      } else {
        try { model.internalModel.focusController.focus(0, 0); } catch (e) { /* ignore */ }
      }
    }

    if (showDebug && now - lastStatusAt > 600) {
      lastStatusAt = now;
      updateDebugStatus();
    }
  }

  function updateDebugStatus() {
    if (!statusEl) return;
    var lines = [];
    if (model) {
      lines.push('<span class="ok">模型</span> ' + escapeHtml(modelName) +
        '  组[' + escapeHtml(modelGroups.join(', ')) + ']  待机=' + escapeHtml(idleGroup || '无'));
      var readback = [];
      try {
        var source = diag.readback || {};
        var names = [
          ['ParamMouthOpenY', source.mouth],
          ['ParamAngleX', source.angleX],
          ['ParamAngleY', source.angleY],
          ['ParamEyeBallX', source.eyeBallX],
          ['ParamEyeBallY', source.eyeBallY]
        ];
        for (var i = 0; i < names.length; i++) {
          if (typeof names[i][1] === 'number' && isFinite(names[i][1])) {
            readback.push(names[i][0].replace('Param', '') + '=' + fmt(names[i][1]));
          }
        }
      } catch (e) { /* ignore */ }
      lines.push('状态=' + live.state + '  口型=' + fmt(live.mouth) + '  眼动=(' +
        fmt(live.gazeX) + ',' + fmt(live.gazeY) + ')  fps=' + fpsCounter.value);
      if (readback.length) lines.push('更新帧参数: ' + escapeHtml(readback.join(' ')));
      lines.push('来源: ' + escapeHtml(modelUrl || ''));
    } else if (demoActive) {
      lines.push('<span class="warn">Canvas 兜底形象</span>（未加载 Live2D 模型）  状态=' + live.state +
        '  口型=' + fmt(demo ? demo.mouth : live.mouth) + '  fps=' + fpsCounter.value);
    } else {
      lines.push('<span class="warn">等待模型…</span>');
    }
    setStatus(lines.join('<br>'));
  }

  // ---------------- 模型加载 ----------------
  function absUrl(path) {
    try { return new URL(path, location.href).href; } catch (e) { return path; }
  }

  function deriveModelName(url) {
    try {
      var path = new URL(url).pathname;
      var file = decodeURIComponent(path.split('/').pop() || 'model');
      return file.replace(/\.model3\.json$/i, '') || 'model';
    } catch (e) {
      return 'model';
    }
  }

  async function resolveModelUrl(raw) {
    var candidates = [];
    if (/^(https?:|data:|blob:)/i.test(raw)) {
      candidates.push(raw);
    } else {
      var cleaned = raw.replace(/^[.\/]+/, '');
      candidates.push(absUrl(raw));               // 相对当前页面
      candidates.push(absUrl('models/' + cleaned)); // 相对 wwwroot/models（发布/复制目录常见布局）
      candidates.push(absUrl('../models/' + cleaned)); // 相对 assets/live2d/models（开发期本地预览）
    }
    var unique = [];
    candidates.forEach(function (c) {
      if (c && unique.indexOf(c) < 0) unique.push(c);
    });
    for (var i = 0; i < unique.length; i++) {
      try {
        var resp = await fetch(unique[i], { cache: 'no-cache' });
        if (resp.ok) return unique[i];
      } catch (e) { /* 尝试下一个候选 */ }
    }
    return null;
  }

  function setupIdleGroup() {
    if (!model) return;
    var mm = model.internalModel.motionManager;
    var definitions = mm.definitions || {};
    var groups = Object.keys(definitions);
    var wanted = String(config.idleMotionGroup || 'Idle').toLowerCase();
    var group = groups.find(function (g) { return g.toLowerCase() === wanted; });
    if (!group) group = groups.find(function (g) { return g.toLowerCase().indexOf('idle') >= 0; });
    if (!group) group = groups[0] || null;

    idleGroup = group;
    try {
      if (config.idleMotion && idleGroup) {
        mm.groups.idle = idleGroup;
        // 立即进入待机动作（之后 MotionManager 会在动作结束后自动随机下一个 Idle 动作）
        model.motion(idleGroup);
      } else {
        mm.groups.idle = '__voxime_idle_disabled__';
      }
    } catch (e) {
      log('待机动作设置失败（模型可能无动作组）', e);
    }
    log('动作组: ' + groups.join(', ') + ' / 待机组: ' + (idleGroup || '无'));
    return groups;
  }

  function hasParameter(coreModel, id) {
    try {
      if (coreModel && typeof coreModel.getParameterIndex === 'function') {
        return coreModel.getParameterIndex(id) >= 0;
      }
    } catch (e) { /* ignore */ }
    return false;
  }

  function setupMouthParameters() {
    if (!model) return;
    var mm = model.internalModel.motionManager;
    var core = model.internalModel.coreModel;
    var declared = [];
    try { declared = (mm.lipSyncIds || []).slice(); } catch (e) { declared = []; }
    if (!declared.length) declared = ['ParamMouthOpenY'];
    var valid = declared.filter(function (id) { return hasParameter(core, id); });
    if (!valid.length) valid = ['ParamMouthOpenY']; // 仅在无法判定/无参数时保留标准 ID，写入时 try/catch
    mouthParamIds = valid;
    log('口型参数: ' + mouthParamIds.join(', '));
  }

  // 每帧在 Cubism 模型更新前写入口型（覆盖动作里可能存在的嘴部曲线）
  // 注意：Cubism 官方流程会在 update() 之后 loadParameters() 还原动作参数，
  // 因此读参验证必须在 write 之后立刻进行（diag.readback 即更新帧内的实际取值）。
  function applyMouthOverride() {
    diag.modelUpdateTicks++;
    if (!model || !config.mouthSync) return;
    try {
      var core = model.internalModel.coreModel;
      for (var i = 0; i < mouthParamIds.length; i++) {
        core.setParameterValueById(mouthParamIds[i], live.mouth);
      }
      diag.mouthWrites++;
      try {
        diag.readback = {
          mouth: core.getParameterValueById(mouthParamIds[0]),
          angleX: core.getParameterValueById('ParamAngleX'),
          angleY: core.getParameterValueById('ParamAngleY'),
          eyeBallX: core.getParameterValueById('ParamEyeBallX'),
          eyeBallY: core.getParameterValueById('ParamEyeBallY')
        };
      } catch (e) { /* 读参失败不影响渲染 */ }
    } catch (e) {
      // 模型缺少对应参数时静默降级，不中断渲染
      diag.mouthErrors++;
      diag.lastMouthError = String((e && e.message) || e);
    }
  }

  async function loadModel(rawUrl) {
    var raw = String(rawUrl || '').trim();
    if (!raw) {
      handleModelError(new Error('模型 URL 为空'), raw);
      return false;
    }
    try {
      hideHint();
      var resolved = await resolveModelUrl(raw);
      if (!resolved) throw new Error('找不到模型文件（已尝试相对路径）: ' + raw);
      setStatus('加载模型中…<br>' + escapeHtml(resolved));

      var app = ensurePixi();
      disposeModel();

      if (!window.PIXI || !PIXI.live2d || !PIXI.live2d.Live2DModel) {
        throw new Error('pixi-live2d-display 未正确加载（vendor/cubism4.min.js）');
      }
      if (!window.Live2DCubismCore) {
        throw new Error('Cubism Core 未加载（vendor/live2dcubismcore.min.js）');
      }

      log('开始加载模型: ' + resolved);
      var loaded = await PIXI.live2d.Live2DModel.from(resolved, {
        autoInteract: false,
        autoUpdate: true,
        motionPreload: 'ALL' // 预加载全部动作：状态动作/待机动作即时可播
      });

      model = loaded;
      modelUrl = resolved;
      modelName = deriveModelName(resolved);
      app.stage.addChild(model);
      model.internalModel.on('beforeModelUpdate', applyMouthOverride);
      setupMouthParameters();
      modelGroups = setupIdleGroup() || [];
      // 初始化时应用缩放与对应的后备分辨率（模型自带尺寸可能很大）。
      resizePixi();

      demoActive = false;
      if (demo) demo.stop();
      demoCanvas.classList.add('hidden');
      app.view.classList.remove('hidden');

      applyModelState(live.state, true);
      setHint('');
      sendHost({
        type: 'modelLoaded',
        name: modelName,
        width: model.internalModel.width || 0,
        height: model.internalModel.height || 0
      });
      setStatus('<span class="ok">模型已加载</span> ' + escapeHtml(modelName) + '<br>' + escapeHtml(resolved));
      log('模型加载完成: ' + modelName);
      return true;
    } catch (err) {
      handleModelError(err, raw);
      return false;
    }
  }

  function disposeModel() {
    if (!model) return;
    try { model.internalModel.off('beforeModelUpdate', applyMouthOverride); } catch (e) { /* ignore */ }
    try { model.destroy(); } catch (e) { /* ignore */ }
    model = null;
    modelGroups = [];
    idleGroup = null;
  }

  function handleModelError(err, raw) {
    var message = (err && err.message) ? err.message : String(err || '未知错误');
    log('模型加载失败: ' + message, '目标: ' + raw);
    setStatus('<span class="err">模型加载失败</span> ' + escapeHtml(message));
    setHint('模型加载失败：' + message, true);
    sendHost({ type: 'error', message: message });

    if (isPreview) {
      // 独立预览页：页面自身始终可展示兜底形象
      showDemo('模型加载失败，已切换 Canvas 兜底形象');
    } else {
      // 嵌入模式：交给 WPF 兜底形象，页面不再绘制（避免与 WPF 层重影）
      demoActive = false;
      if (demo) demo.stop();
      demoCanvas.classList.add('hidden');
      if (pixiApp) pixiApp.view.classList.add('hidden');
      setHint('');
    }
  }

  // ---------------- 状态 / 表情 / 动作 ----------------
  function applyModelState(stateName, force) {
    var state = normalizeState(stateName);
    var previous = live.state;
    live.state = state;
    if (!model) {
      if (demo) demo.setState(state);
      return;
    }
    if (previous === state && !force) return;

    // 1) 表情：配置提供时应用；模型不含该表情时静默降级。
    var expression = stateExpressions[state];
    try {
      if (expression) {
        model.expression(expression);
      } else if (state === 'idle' && model.internalModel.motionManager.expressionManager) {
        model.internalModel.motionManager.expressionManager.resetExpression();
      }
    } catch (e) {
      log('表情应用失败（忽略）: ' + state + ' / ' + expression, e);
    }

    // 2) 动作：模型若存在与状态同名的动作组则播放（如 Listening/Thinking/Speaking/Error/TapBody），
    //    否则保持待机动作，不报错。
    try {
      var mm = model.internalModel.motionManager;
      var definitions = mm.definitions || {};
      var groups = Object.keys(definitions);
      var candidates = [state, state.charAt(0).toUpperCase() + state.slice(1)];
      var group = null;
      for (var c = 0; c < candidates.length && !group; c++) {
        for (var g = 0; g < groups.length; g++) {
          if (groups[g].toLowerCase() === candidates[c].toLowerCase()) { group = groups[g]; break; }
        }
      }
      if (group) model.motion(group);
    } catch (e) {
      log('状态动作应用失败（忽略）: ' + state, e);
    }
  }

  function setState(name) {
    applyModelState(name, false);
    updateStateButtons(live.state);
    if (isPreview) updateDebugStatus();
  }

  function setMouth(value) {
    live.targetMouth = clamp01(value);
    if (!config.mouthSync) live.mouth = 0;
    if (demo) demo.setMouth(live.targetMouth);
    if (isPreview) {
      var slider = document.getElementById('mouth-slider');
      var label = document.getElementById('mouth-value');
      if (slider) slider.value = String(live.targetMouth);
      if (label) label.textContent = fmt(live.targetMouth);
    }
  }

  function setGaze(x, y) {
    live.targetGazeX = clampGaze(x);
    live.targetGazeY = clampGaze(y);
    if (demo) demo.setGaze(live.targetGazeX, live.targetGazeY);
    if (isPreview) {
      var sx = document.getElementById('gaze-x');
      var sy = document.getElementById('gaze-y');
      if (sx) sx.value = String(live.targetGazeX);
      if (sy) sy.value = String(live.targetGazeY);
    }
  }

  function applyConfig(msg) {
    if (!msg || typeof msg !== 'object') return;
    var idleMotionChanged = msg.idleMotion !== undefined && !!msg.idleMotion !== config.idleMotion;
    var scaleChanged = typeof msg.scale === 'number' && isFinite(msg.scale)
      && Math.abs(Math.min(3, Math.max(0.2, msg.scale)) - config.scale) > 0.0001;
    if (msg.eyeFollow !== undefined) config.eyeFollow = !!msg.eyeFollow;
    if (msg.idleMotion !== undefined) config.idleMotion = !!msg.idleMotion;
    if (msg.mouthSync !== undefined) config.mouthSync = !!msg.mouthSync;
    if (typeof msg.scale === 'number' && isFinite(msg.scale)) config.scale = Math.min(3, Math.max(0.2, msg.scale));
    if (msg.idleMotionGroup) config.idleMotionGroup = String(msg.idleMotionGroup);
    if (msg.showBubble !== undefined) config.showBubble = !!msg.showBubble;
    if (msg.expressions && typeof msg.expressions === 'object') {
      Object.keys(stateExpressions).forEach(function (key) {
        if (msg.expressions[key]) stateExpressions[key] = String(msg.expressions[key]);
      });
    }
    if (!config.mouthSync) live.mouth = 0;
    if (model) {
      // 待机动画开关切换时：关闭要立即停掉正在播放的动作，否则“关闭待机动画”只是不再随机下一个，
      // 当前动作会一直循环，导致眼动/表情被动作覆盖、看起来像没有生效。
      if (idleMotionChanged && !config.idleMotion) {
        try {
          model.internalModel.motionManager.stopAllMotions();
        } catch (e) {
          log('停止待机动作失败（忽略）', e);
        }
      }
      setupIdleGroup();
      if (scaleChanged) {
        resizePixi();
      } else {
        layoutModel();
      }
    }
    syncConfigCheckboxes();
    if (isPreview) updateDebugStatus();
  }

  // ---------------- 气泡 ----------------
  function setBubble(text) {
    if (!bubbleEl) return;
    var value = text == null ? '' : String(text);
    if (!value || !config.showBubble) {
      bubbleEl.classList.remove('visible');
      bubbleEl.textContent = '';
      return;
    }
    if (!config.bubbleInPage) {
      // 嵌入模式下气泡由 WPF 层负责，页面只记录不显示（避免重影）
      return;
    }
    bubbleEl.textContent = value;
    bubbleEl.classList.add('visible');
  }

  function hideHint() {
    setHint('');
  }

  // ---------------- 宿主消息桥 ----------------
  function handleHostMessage(event) {
    var data = event && event.data;
    if (typeof data === 'string') {
      try { data = JSON.parse(data); } catch (e) { log('无法解析宿主消息', data); return; }
    }
    if (!data || typeof data !== 'object' || !data.type) return;
    switch (data.type) {
      case 'loadModel':
        loadModel(data.url);
        break;
      case 'setState':
        setState(data.state);
        break;
      case 'setMouth':
        setMouth(data.value);
        break;
      case 'setGaze':
        setGaze(data.x, data.y);
        break;
      case 'setBubble':
        setBubble(data.text);
        break;
      case 'setConfig':
        applyConfig(data);
        break;
      default:
        log('未知宿主消息类型: ' + data.type);
    }
  }

  // ---------------- Canvas 兜底形象 ----------------
  function DemoPet(canvas) {
    this.canvas = canvas;
    this.ctx = canvas.getContext('2d');
    this.width = 0;
    this.height = 0;
    this.dpr = 1;
    this.time = 0;
    this.running = false;
    this.state = 'idle';
    this.mouth = 0;
    this.mouthTarget = 0;
    this.gazeX = 0;
    this.gazeY = 0;
    this.blinkAt = 2.2 + Math.random() * 2;
    this.blinkUntil = -1;
    this.sparkles = [];
    for (var i = 0; i < 3; i++) {
      this.sparkles.push({ phase: Math.random() * Math.PI * 2, speed: 0.6 + Math.random() * 0.7, radius: 0.5 + Math.random() * 0.8 });
    }
    this.resize();
  }

  DemoPet.prototype.resize = function () {
    var w = Math.max(1, stageEl.clientWidth);
    var h = Math.max(1, stageEl.clientHeight);
    this.dpr = Math.min(window.devicePixelRatio || 1, 2);
    this.width = w;
    this.height = h;
    this.canvas.width = Math.round(w * this.dpr);
    this.canvas.height = Math.round(h * this.dpr);
    this.ctx.setTransform(this.dpr, 0, 0, this.dpr, 0, 0);
  };

  DemoPet.prototype.setState = function (state) {
    this.state = normalizeState(state);
  };

  DemoPet.prototype.setMouth = function (value) {
    this.mouthTarget = clamp01(value);
  };

  DemoPet.prototype.setGaze = function (x, y) {
    this.gazeX = clampGaze(x);
    this.gazeY = clampGaze(y);
  };

  DemoPet.prototype.start = function () {
    if (this.running) {
      return;
    }
    this.running = true;
    var self = this;
    var last = performance.now();
    function frame(now) {
      if (!self.running) {
        return;
      }
      var deltaSeconds = Math.min(0.1, Math.max(0, (now - last) / 1000));
      last = now;
      // 兜底演示模式下同样维护 fps 统计
      fpsCounter.frames++;
      if (now - fpsCounter.since >= 1000) {
        fpsCounter.value = Math.round(fpsCounter.frames * 1000 / (now - fpsCounter.since));
        fpsCounter.frames = 0;
        fpsCounter.since = now;
      }
      self.tick(deltaSeconds);
      requestAnimationFrame(frame);
    }
    requestAnimationFrame(frame);
  };

  DemoPet.prototype.stop = function () {
    this.running = false;
  };

  DemoPet.prototype.tick = function (dt) {
    if (!this.running) return;
    this.time += dt;
    this.mouth += (this.mouthTarget - this.mouth) * Math.min(1, dt * 14);

    // 眨眼调度
    if (this.blinkUntil < 0 && this.time >= this.blinkAt) {
      this.blinkUntil = this.time + 0.16;
    }
    if (this.blinkUntil > 0 && this.time >= this.blinkUntil) {
      this.blinkUntil = -1;
      this.blinkAt = this.time + 2.6 + Math.random() * 3.4;
    }

    this.draw();
  };

  DemoPet.prototype.blinkFactor = function () {
    if (this.blinkUntil < 0) return 1;
    var remaining = this.blinkUntil - this.time;
    var open = Math.max(0, Math.min(1, Math.abs(remaining - 0.08) / 0.08));
    return open;
  };

  DemoPet.prototype.draw = function () {
    var ctx = this.ctx;
    var w = this.width;
    var h = this.height;
    ctx.clearRect(0, 0, w, h);

    var t = this.time;
    var state = this.state;
    var r = Math.max(38, Math.min(w, h) * 0.21);
    var cx = w / 2;
    var cy = h * 0.53;
    var bob = Math.sin(t * 1.7) * r * 0.045;
    var shake = state === 'error' ? Math.sin(t * 42) * 3.2 : 0;

    ctx.save();
    ctx.translate(cx + shake, cy + bob);

    // 影子
    ctx.save();
    ctx.translate(0, r * 1.06);
    var shadow = ctx.createRadialGradient(0, 0, r * 0.1, 0, 0, r * 0.95);
    shadow.addColorStop(0, 'rgba(24, 12, 60, 0.30)');
    shadow.addColorStop(1, 'rgba(24, 12, 60, 0)');
    ctx.fillStyle = shadow;
    ctx.beginPath();
    ctx.ellipse(0, 0, r * 0.95, r * 0.24, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    // 聆听：扩散波纹
    if (state === 'listening') {
      for (var i = 0; i < 3; i++) {
        var phase = (t * 0.7 + i / 3) % 1;
        var radius = r * (0.95 + phase * 0.75);
        ctx.beginPath();
        ctx.arc(0, -r * 0.05, radius, 0, Math.PI * 2);
        ctx.strokeStyle = 'rgba(179, 157, 255, ' + fmt(0.42 * (1 - phase), 3) + ')';
        ctx.lineWidth = 2.2;
        ctx.stroke();
      }
    }

    // 呼吸缩放
    var breath = 1 + Math.sin(t * 1.9) * 0.018 + (state === 'thinking' ? Math.sin(t * 6) * 0.006 : 0);
    ctx.save();
    ctx.scale(breath, breath);

    // 耳朵 / 触角
    var earSwing = Math.sin(t * 1.9 + 0.7) * 0.08;
    this.drawEar(ctx, r, -1, earSwing, t);
    this.drawEar(ctx, r, 1, earSwing, t);

    // 身体
    var bodyGrad = ctx.createRadialGradient(-r * 0.34, -r * 0.40, r * 0.1, 0, 0, r * 1.05);
    bodyGrad.addColorStop(0, '#d3c4ff');
    bodyGrad.addColorStop(0.42, '#9b7cff');
    bodyGrad.addColorStop(0.78, '#7c5cff');
    bodyGrad.addColorStop(1, '#4f37b8');
    ctx.fillStyle = bodyGrad;
    ctx.beginPath();
    ctx.arc(0, 0, r, 0, Math.PI * 2);
    ctx.fill();

    // 外发光边缘
    ctx.strokeStyle = 'rgba(255,255,255,0.10)';
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(0, 0, r - 1, 0, Math.PI * 2);
    ctx.stroke();

    // 高光
    ctx.save();
    ctx.beginPath();
    ctx.ellipse(-r * 0.34, -r * 0.44, r * 0.32, r * 0.22, -0.55, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(255,255,255,0.30)';
    ctx.fill();
    ctx.restore();

    // 腮红
    ctx.save();
    ctx.fillStyle = 'rgba(255, 130, 170, 0.30)';
    ctx.beginPath();
    ctx.ellipse(-r * 0.58, r * 0.18, r * 0.15, r * 0.10, -0.1, 0, Math.PI * 2);
    ctx.ellipse(r * 0.58, r * 0.18, r * 0.15, r * 0.10, 0.1, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    // 眼睛
    this.drawEye(ctx, r, -1, t);
    this.drawEye(ctx, r, 1, t);

    // 嘴
    this.drawMouth(ctx, r);

    // 思考：轨道圆点
    if (state === 'thinking') {
      for (var d = 0; d < 3; d++) {
        var angle = t * 2.4 + d * (Math.PI * 2 / 3);
        var ox = Math.cos(angle) * r * 1.25;
        var oy = Math.sin(angle) * r * 0.5 - r * 0.35;
        ctx.beginPath();
        ctx.arc(ox, oy, r * 0.075, 0, Math.PI * 2);
        ctx.fillStyle = 'rgba(232, 232, 240, ' + fmt(0.55 + 0.4 * Math.sin(angle), 2) + ')';
        ctx.fill();
      }
    }

    // 说话：声波
    if (state === 'speaking' && this.mouth > 0.12) {
      for (var s = 0; s < 2; s++) {
        var side = s === 0 ? -1 : 1;
        ctx.beginPath();
        ctx.arc(side * r * 0.92, r * 0.08, r * (0.18 + this.mouth * 0.16), -Math.PI * 0.42, Math.PI * 0.42);
        ctx.strokeStyle = 'rgba(179, 157, 255, ' + fmt(0.30 + this.mouth * 0.4, 2) + ')';
        ctx.lineWidth = 2.5;
        ctx.stroke();
      }
    }

    // 出错：红色氛围
    if (state === 'error') {
      ctx.save();
      ctx.globalCompositeOperation = 'source-atop';
      ctx.fillStyle = 'rgba(255, 80, 90, 0.20)';
      ctx.beginPath();
      ctx.arc(0, 0, r, 0, Math.PI * 2);
      ctx.fill();
      ctx.restore();
    }

    ctx.restore(); // breath

    // 闪亮星点
    for (var k = 0; k < this.sparkles.length; k++) {
      var sp = this.sparkles[k];
      var px = Math.sin(t * sp.speed + sp.phase) * r * sp.radius;
      var py = -r * (0.9 + 0.25 * Math.cos(t * sp.speed * 0.8 + sp.phase));
      var size = r * (0.035 + 0.02 * Math.sin(t * 3 + sp.phase));
      ctx.save();
      ctx.translate(px, py);
      ctx.rotate(t * 0.6 + sp.phase);
      ctx.fillStyle = 'rgba(255, 250, 220, ' + fmt(0.35 + 0.3 * Math.sin(t * 2.3 + sp.phase), 2) + ')';
      this.drawStar(ctx, size);
      ctx.restore();
    }

    ctx.restore(); // translate
  };

  DemoPet.prototype.drawEar = function (ctx, r, side, swing, t) {
    ctx.save();
    ctx.translate(side * r * 0.52, -r * 0.86);
    ctx.rotate(side * (0.24 + swing));
    var grad = ctx.createLinearGradient(0, 0, 0, -r * 0.75);
    grad.addColorStop(0, '#7c5cff');
    grad.addColorStop(1, '#c4b2ff');
    ctx.fillStyle = grad;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.quadraticCurveTo(side * r * 0.20, -r * 0.55, side * r * 0.02, -r * 0.78);
    ctx.quadraticCurveTo(-side * r * 0.22, -r * 0.32, 0, 0);
    ctx.fill();
    ctx.restore();
  };

  DemoPet.prototype.drawEye = function (ctx, r, side, t) {
    var ex = side * r * 0.36;
    var ey = -r * 0.10;
    var ew = r * 0.20;
    var eh = r * 0.25 * this.blinkFactor();
    if (eh < 1) eh = 1;

    if (this.state === 'error') {
      // 错误态：X 形眼睛
      ctx.strokeStyle = 'rgba(255, 235, 235, 0.92)';
      ctx.lineWidth = Math.max(2, r * 0.045);
      ctx.beginPath();
      ctx.moveTo(ex - ew * 0.5, ey - eh * 0.5);
      ctx.lineTo(ex + ew * 0.5, ey + eh * 0.5);
      ctx.moveTo(ex + ew * 0.5, ey - eh * 0.5);
      ctx.lineTo(ex - ew * 0.5, ey + eh * 0.5);
      ctx.stroke();
      return;
    }

    // 眼白
    ctx.fillStyle = 'rgba(255,255,255,0.95)';
    ctx.beginPath();
    ctx.ellipse(ex, ey, ew, eh, 0, 0, Math.PI * 2);
    ctx.fill();

    // 瞳孔（跟随 gaze）
    var px = ex + this.gazeX * ew * 0.42;
    var py = ey + this.gazeY * eh * 0.32;
    var pupil = Math.min(ew, eh) * 0.55;
    ctx.fillStyle = '#3b2a7a';
    ctx.beginPath();
    ctx.arc(px, py, pupil, 0, Math.PI * 2);
    ctx.fill();

    // 瞳孔高光
    ctx.fillStyle = 'rgba(255,255,255,0.92)';
    ctx.beginPath();
    ctx.arc(px - pupil * 0.32, py - pupil * 0.35, pupil * 0.30, 0, Math.PI * 2);
    ctx.fill();
  };

  DemoPet.prototype.drawMouth = function (ctx, r) {
    var my = r * 0.30;
    var mw = r * 0.22;
    var open = this.mouth;
    if (open > 0.06) {
      var mh = r * (0.05 + open * 0.30);
      ctx.fillStyle = '#4a1230';
      ctx.beginPath();
      ctx.ellipse(0, my + mh * 0.2, mw * (0.75 + open * 0.25), mh, 0, 0, Math.PI * 2);
      ctx.fill();
      // 舌头
      ctx.fillStyle = '#ff8fa8';
      ctx.beginPath();
      ctx.ellipse(0, my + mh * 0.72, mw * 0.5, mh * 0.42, 0, 0, Math.PI * 2);
      ctx.fill();
    } else {
      ctx.strokeStyle = 'rgba(60, 30, 90, 0.85)';
      ctx.lineWidth = Math.max(1.6, r * 0.028);
      ctx.lineCap = 'round';
      ctx.beginPath();
      ctx.arc(0, my - r * 0.02, mw * 0.7, Math.PI * 0.18, Math.PI * 0.82);
      ctx.stroke();
    }
  };

  DemoPet.prototype.drawStar = function (ctx, size) {
    ctx.beginPath();
    for (var i = 0; i < 8; i++) {
      var angle = i * Math.PI / 4;
      var radius = i % 2 === 0 ? size : size * 0.34;
      var x = Math.cos(angle) * radius;
      var y = Math.sin(angle) * radius;
      if (i === 0) ctx.moveTo(x, y); else ctx.lineTo(x, y);
    }
    ctx.closePath();
    ctx.fill();
  };

  function showDemo(reason) {
    if (!demoCanvas) return;
    if (!demo) demo = new DemoPet(demoCanvas);
    demo.resize();
    demo.setState(live.state);
    demo.setMouth(live.targetMouth);
    demo.setGaze(live.gazeX, live.gazeY);
    demoActive = true;
    demoCanvas.classList.remove('hidden');
    if (pixiApp) pixiApp.view.classList.add('hidden');
    demo.start();
    if (reason) {
      setHint(reason, false);
      if (isPreview) setStatus('<span class="warn">Canvas 兜底形象</span> ' + escapeHtml(reason));
    }
    log('显示 Canvas 兜底形象: ' + (reason || ''));
  }

  // ---------------- 预览页 UI ----------------
  function updateStateButtons(state) {
    var buttons = document.querySelectorAll('.state-btn');
    for (var i = 0; i < buttons.length; i++) {
      buttons[i].classList.toggle('active', buttons[i].getAttribute('data-state') === state);
    }
  }

  function syncConfigCheckboxes() {
    var map = { 'chk-eyefollow': 'eyeFollow', 'chk-idlemotion': 'idleMotion', 'chk-mouthsync': 'mouthSync' };
    Object.keys(map).forEach(function (id) {
      var el = document.getElementById(id);
      if (el) el.checked = !!config[map[id]];
    });
  }

  function setVisibleStateButton(state) {
    updateStateButtons(state);
    if (isPreview) {
      // 预览页点击状态按钮时也给出对应口型/表情观感
      if (state === 'speaking') setMouth(0.6);
      else if (state === 'listening') setMouth(0);
    }
  }

  function readUrlParams() {
    var bg = params.get('bg');
    if (bg) setBackground(bg);
    var state = params.get('state');
    if (state) live.state = normalizeState(state);
    var mouth = params.get('mouth');
    if (mouth != null && mouth !== '') setMouth(parseFloat(mouth));
    var idleMotionParam = params.get('idlemotion');
    if (idleMotionParam !== null) config.idleMotion = idleMotionParam !== '0';
    var gaze = params.get('gaze');
    if (gaze) {
      var parts = gaze.split(',');
      if (parts.length === 2) {
        setGaze(parseFloat(parts[0]), parseFloat(parts[1]));
      }
    }
    var bubble = params.get('bubble');
    if (bubble) setTimeout(function () { setBubble(bubble); }, 500);
  }

  function setBackground(name) {
    document.body.classList.remove('bg-checker', 'bg-solid', 'bg-desktop', 'bg-transparent');
    document.body.classList.add('bg-' + (name || 'checker'));
    var select = document.getElementById('bg-select');
    if (select) select.value = name || 'checker';
  }

  function wirePreviewUi() {
    if (!isPreview) return;
    var modelInput = document.getElementById('model-input');
    var loadButton = document.getElementById('btn-load');
    if (loadButton) {
      loadButton.addEventListener('click', function () {
        var value = modelInput ? modelInput.value.trim() : '';
        if (value) loadModel(value);
      });
    }
    if (modelInput) {
      modelInput.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') {
          event.preventDefault();
          if (loadButton) loadButton.click();
        }
      });
    }

    var stateButtons = document.querySelectorAll('.state-btn');
    for (var i = 0; i < stateButtons.length; i++) {
      stateButtons[i].addEventListener('click', function () {
        var state = normalizeState(this.getAttribute('data-state'));
        applyModelState(state, false);
        setVisibleStateButton(state);
      });
    }

    var mouthSlider = document.getElementById('mouth-slider');
    if (mouthSlider) {
      mouthSlider.addEventListener('input', function () {
        setMouth(parseFloat(this.value));
        var label = document.getElementById('mouth-value');
        if (label) label.textContent = fmt(live.targetMouth);
      });
    }
    var gazeX = document.getElementById('gaze-x');
    var gazeY = document.getElementById('gaze-y');
    function gazeChanged() {
      setGaze(gazeX ? parseFloat(gazeX.value) : 0, gazeY ? parseFloat(gazeY.value) : 0);
    }
    if (gazeX) gazeX.addEventListener('input', gazeChanged);
    if (gazeY) gazeY.addEventListener('input', gazeChanged);

    var eyeFollow = document.getElementById('chk-eyefollow');
    var idleMotion = document.getElementById('chk-idlemotion');
    var mouthSync = document.getElementById('chk-mouthsync');
    if (eyeFollow) eyeFollow.addEventListener('change', function () { applyConfig({ eyeFollow: this.checked }); });
    if (idleMotion) idleMotion.addEventListener('change', function () { applyConfig({ idleMotion: this.checked }); });
    if (mouthSync) mouthSync.addEventListener('change', function () { applyConfig({ mouthSync: this.checked }); });

    var bubbleInput = document.getElementById('bubble-input');
    if (bubbleInput) {
      bubbleInput.addEventListener('keydown', function (event) {
        if (event.key === 'Enter') setBubble(this.value);
      });
    }
    var hideBubble = document.getElementById('btn-bubble-hide');
    if (hideBubble) hideBubble.addEventListener('click', function () { setBubble(null); });

    var demoButton = document.getElementById('btn-demo');
    if (demoButton) {
      demoButton.addEventListener('click', function () {
        disposeModel();
        if (pixiApp) pixiApp.view.classList.add('hidden');
        showDemo('已切换到 Canvas 兜底演示');
      });
    }

    var bgSelect = document.getElementById('bg-select');
    if (bgSelect) bgSelect.addEventListener('change', function () { setBackground(this.value); });

    // 鼠标位置驱动眼动（独立预览时方便手动验证）
    stageEl.addEventListener('mousemove', function (event) {
      if (!config.eyeFollow) return;
      var rect = stageEl.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      setGaze(
        ((event.clientX - rect.left) / rect.width) * 2 - 1,
        ((event.clientY - rect.top) / rect.height) * 2 - 1
      );
    });
  }

  // ---------------- 启动 ----------------
  function boot() {
    log('页面启动 v' + VERSION + ' page=' + page + ' embedded=' + embedded);

    readUrlParams();
    wirePreviewUi();
    syncConfigCheckboxes();
    updateStateButtons(live.state);
    updateDebugStatus();

    // 监听 WPF 宿主消息
    if (hasHost()) {
      window.chrome.webview.addEventListener('message', handleHostMessage);
      log('已连接 WPF 宿主消息桥');
    }

    var resizeTimer = 0;
    function scheduleResize() {
      clearTimeout(resizeTimer);
      resizeTimer = setTimeout(function () {
        resizePixi();
        if (demo) demo.resize();
      }, 60);
    }
    window.addEventListener('resize', scheduleResize);
    if (window.ResizeObserver) {
      try { new ResizeObserver(scheduleResize).observe(stageEl); } catch (e) { /* ignore */ }
    }

    // 通知宿主：页面脚本就绪（C# 等待 ready 后下发 setConfig/loadModel）
    sendReady();

    // 独立打开时根据 URL 自动加载模型；否则显示兜底形象
    var modelParam = params.get('model');
    if (isPreview && modelParam) {
      var modelInput = document.getElementById('model-input');
      if (modelInput) modelInput.value = modelParam;
    }
    if (!forceDemo && modelParam) {
      loadModel(modelParam).then(function (ok) {
        if (!ok && isPreview) showDemo('模型加载失败，显示兜底形象');
      });
    } else if (!forceDemo && !embedded && !hasHost()) {
      // 尝试默认示例模型（存在则自动加载，方便本地预览/截图）
      var defaultModel = '../models/Hiyori/Hiyori.model3.json';
      loadModel(defaultModel).then(function (ok) {
        if (!ok) showDemo('未找到示例模型，显示 Canvas 兜底形象');
      });
    } else if (embedded) {
      // 嵌入模式：兜底形象由 WPF 层负责，页面保持完全透明，等待 C# 下发 loadModel。
      setHint('');
    } else {
      // 未指定模型（独立预览/开发页）：直接展示 Canvas 兜底形象，验证“无模型”页面行为。
      showDemo(forceDemo ? 'Canvas 兜底演示（未加载模型）' : '等待宿主下发模型…');
    }

    // 预处理：C# 可能在 ready 后立刻下发消息，这里确保配置立即生效
    if (!isPreview && params.get('state')) applyModelState(live.state, true);
    if (!isPreview) setMouth(live.targetMouth);
    if (!isPreview) setGaze(live.gazeX, live.gazeY);
  }

  // 暴露少量调试接口（headless 验证/开发者控制台用）
  window.__voxime = {
    version: VERSION,
    loadModel: loadModel,
    setState: setState,
    setMouth: setMouth,
    setGaze: setGaze,
    setBubble: setBubble,
    setConfig: applyConfig,
    showDemo: showDemo,
    debug: function () {
      var result = {
        page: page,
        embedded: embedded,
        model: modelName,
        modelUrl: modelUrl,
        state: live.state,
        mouth: live.mouth,
        targetMouth: live.targetMouth,
        gazeX: live.gazeX,
        gazeY: live.gazeY,
        targetGazeX: live.targetGazeX,
        targetGazeY: live.targetGazeY,
        groups: modelGroups,
        idleGroup: idleGroup,
        demoActive: demoActive,
        fps: fpsCounter.value,
        mouthSync: config.mouthSync,
        mouthParamIds: mouthParamIds,
        diag: diag
      };
      try {
        if (model) {
          result.params = {};
          var ids = ['ParamMouthOpenY', 'ParamAngleX', 'ParamAngleY', 'ParamEyeBallX', 'ParamEyeBallY'];
          for (var i = 0; i < ids.length; i++) {
            try { result.params[ids[i]] = model.internalModel.coreModel.getParameterValueById(ids[i]); } catch (e) { /* ignore */ }
          }
          try {
            var mm = model.internalModel.motionManager;
            result.motion = {
              playing: !!mm.playing,
              group: mm.state ? mm.state.currentGroup : null,
              index: mm.state ? mm.state.currentIndex : null
            };
          } catch (e) { result.motion = null; }
        }
      } catch (e) { /* ignore */ }
      return result;
    }
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', boot);
  } else {
    boot();
  }
})();
