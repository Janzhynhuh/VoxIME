/* ============================================================
 * SMAA 1x（Subpixel Morphological Antialiasing）实验实现
 *
 * 基于 three.js r160 examples/jsm/shaders/SMAAShader.js 的 WebGL 端口，
 * 原始算法：https://github.com/iryoku/smaa （MIT / zlib 许可）。
 * SMAA 1x Medium：颜色边缘检测 + 8 步搜索 + 邻域混合。
 *
 * 适配 PixiJS 8：三个 pass 由 SMAAFilter.apply 串起来；
 * 输入是 Live2D 模型的透明画布，边缘检测同时考虑 alpha，
 * 混合在预乘 alpha 空间进行，避免透明背景产生黑边。
 * ============================================================ */
(function () {
  'use strict';

  if (!window.PIXI) {
    return;
  }

  var EDGE_FRAGMENT = [
    'precision highp float;',
    'in vec2 vTextureCoord;',
    'out vec4 finalColor;',
    'uniform sampler2D uTexture;',
    'uniform vec4 uInputSize;',
    '#define SMAA_THRESHOLD 0.1',
    'float edgeDelta(vec4 a, vec4 b) {',
    '  vec4 d = abs(a - b);',
    '  return max(max(d.r, d.g), max(d.b, d.a));',
    '}',
    'void main() {',
    '  vec2 texel = 1.0 / uInputSize.xy;',
    '  vec2 uv = vTextureCoord;',
    '  vec4 C = texture(uTexture, uv);',
    '  vec4 Cl = texture(uTexture, uv + vec2(-texel.x, 0.0));',
    '  vec4 Ct = texture(uTexture, uv + vec2(0.0, texel.y));',
    '  float dx = edgeDelta(C, Cl);',
    '  float dy = edgeDelta(C, Ct);',
    '  vec2 edges = step(vec2(SMAA_THRESHOLD), vec2(dx, dy));',
    '  if (dot(edges, vec2(1.0, 1.0)) == 0.0) {',
    '    discard;',
    '  }',
    '  vec4 Cr = texture(uTexture, uv + vec2(texel.x, 0.0));',
    '  vec4 Cb = texture(uTexture, uv + vec2(0.0, -texel.y));',
    '  float maxDelta = max(max(dx, dy), max(edgeDelta(C, Cr), edgeDelta(C, Cb)));',
    '  vec4 Cll = texture(uTexture, uv + vec2(-2.0 * texel.x, 0.0));',
    '  vec4 Ctt = texture(uTexture, uv + vec2(0.0, 2.0 * texel.y));',
    '  maxDelta = max(maxDelta, max(edgeDelta(C, Cll), edgeDelta(C, Ctt)));',
    '  edges *= step(0.5 * maxDelta, vec2(dx, dy));',
    '  finalColor = vec4(edges, 0.0, 0.0);',
    '}'
  ].join('\n');

  var WEIGHT_FRAGMENT = [
    'precision highp float;',
    'in vec2 vTextureCoord;',
    'out vec4 finalColor;',
    'uniform sampler2D uTexture;',
    'uniform sampler2D uArea;',
    'uniform sampler2D uSearch;',
    'uniform vec4 uInputSize;',
    '#define SMAA_MAX_SEARCH_STEPS 8',
    '#define SMAA_AREATEX_MAX_DISTANCE 16',
    '#define SMAA_AREATEX_PIXEL_SIZE (1.0 / vec2(160.0, 560.0))',
    '#define SMAA_AREATEX_SUBTEX_SIZE (1.0 / 7.0)',
    'vec2 resolution;',
    'vec2 pixcoord;',
    '#define SMAASampleLevelZeroOffset(tex, coord, offset) texture(tex, coord + vec2(offset) * resolution)',
    'float SMAASearchLength(sampler2D searchTex, vec2 e, float bias, float scale) {',
    '  e.r = bias + e.r * scale;',
    '  return 255.0 * texture(searchTex, e).r;',
    '}',
    'float SMAASearchXLeft(sampler2D edgesTex, sampler2D searchTex, vec2 texcoord, float end) {',
    '  vec2 e = vec2(0.0, 1.0);',
    '  for (int i = 0; i < SMAA_MAX_SEARCH_STEPS; i++) {',
    '    e = texture(edgesTex, texcoord).rg;',
    '    texcoord -= vec2(2.0, 0.0) * resolution;',
    '    if (!(texcoord.x > end && e.g > 0.8281 && e.r == 0.0)) break;',
    '  }',
    '  texcoord.x += 0.25 * resolution.x;',
    '  texcoord.x += resolution.x;',
    '  texcoord.x += 2.0 * resolution.x;',
    '  texcoord.x -= resolution.x * SMAASearchLength(searchTex, e, 0.0, 0.5);',
    '  return texcoord.x;',
    '}',
    'float SMAASearchXRight(sampler2D edgesTex, sampler2D searchTex, vec2 texcoord, float end) {',
    '  vec2 e = vec2(0.0, 1.0);',
    '  for (int i = 0; i < SMAA_MAX_SEARCH_STEPS; i++) {',
    '    e = texture(edgesTex, texcoord).rg;',
    '    texcoord += vec2(2.0, 0.0) * resolution;',
    '    if (!(texcoord.x < end && e.g > 0.8281 && e.r == 0.0)) break;',
    '  }',
    '  texcoord.x -= 0.25 * resolution.x;',
    '  texcoord.x -= resolution.x;',
    '  texcoord.x -= 2.0 * resolution.x;',
    '  texcoord.x += resolution.x * SMAASearchLength(searchTex, e, 0.5, 0.5);',
    '  return texcoord.x;',
    '}',
    'float SMAASearchYUp(sampler2D edgesTex, sampler2D searchTex, vec2 texcoord, float end) {',
    '  vec2 e = vec2(1.0, 0.0);',
    '  for (int i = 0; i < SMAA_MAX_SEARCH_STEPS; i++) {',
    '    e = texture(edgesTex, texcoord).rg;',
    '    texcoord += vec2(0.0, 2.0) * resolution;',
    '    if (!(texcoord.y > end && e.r > 0.8281 && e.g == 0.0)) break;',
    '  }',
    '  texcoord.y -= 0.25 * resolution.y;',
    '  texcoord.y -= resolution.y;',
    '  texcoord.y -= 2.0 * resolution.y;',
    '  texcoord.y += resolution.y * SMAASearchLength(searchTex, e.gr, 0.0, 0.5);',
    '  return texcoord.y;',
    '}',
    'float SMAASearchYDown(sampler2D edgesTex, sampler2D searchTex, vec2 texcoord, float end) {',
    '  vec2 e = vec2(1.0, 0.0);',
    '  for (int i = 0; i < SMAA_MAX_SEARCH_STEPS; i++) {',
    '    e = texture(edgesTex, texcoord).rg;',
    '    texcoord -= vec2(0.0, 2.0) * resolution;',
    '    if (!(texcoord.y < end && e.r > 0.8281 && e.g == 0.0)) break;',
    '  }',
    '  texcoord.y += 0.25 * resolution.y;',
    '  texcoord.y += resolution.y;',
    '  texcoord.y += 2.0 * resolution.y;',
    '  texcoord.y -= resolution.y * SMAASearchLength(searchTex, e.gr, 0.5, 0.5);',
    '  return texcoord.y;',
    '}',
    'vec2 SMAAArea(sampler2D areaTex, vec2 dist, float e1, float e2, float offset) {',
    '  vec2 texcoord = float(SMAA_AREATEX_MAX_DISTANCE) * floor(4.0 * vec2(e1, e2) + 0.5) + dist;',
    '  texcoord = SMAA_AREATEX_PIXEL_SIZE * texcoord + (0.5 * SMAA_AREATEX_PIXEL_SIZE);',
    '  texcoord.y += SMAA_AREATEX_SUBTEX_SIZE * offset;',
    '  return texture(areaTex, texcoord).rg;',
    '}',
    'vec4 SMAABlendingWeightCalculationPS(vec2 texcoord, vec2 pixcoord, vec4 offset[3], sampler2D edgesTex, sampler2D areaTex, sampler2D searchTex, ivec4 subsampleIndices) {',
    '  vec4 weights = vec4(0.0);',
    '  vec2 e = texture(edgesTex, texcoord).rg;',
    '  if (e.g > 0.0) {',
    '    vec2 d;',
    '    vec2 coords;',
    '    coords.x = SMAASearchXLeft(edgesTex, searchTex, offset[0].xy, offset[2].x);',
    '    coords.y = offset[1].y;',
    '    d.x = coords.x;',
    '    float e1 = texture(edgesTex, coords).r;',
    '    coords.x = SMAASearchXRight(edgesTex, searchTex, offset[0].zw, offset[2].y);',
    '    d.y = coords.x;',
    '    d = d / resolution.x - pixcoord.x;',
    '    vec2 sqrt_d = sqrt(abs(d));',
    '    coords.y -= 1.0 * resolution.y;',
    '    float e2 = SMAASampleLevelZeroOffset(edgesTex, coords, ivec2(1, 0)).r;',
    '    weights.rg = SMAAArea(areaTex, sqrt_d, e1, e2, float(subsampleIndices.y));',
    '  }',
    '  if (e.r > 0.0) {',
    '    vec2 d;',
    '    vec2 coords;',
    '    coords.y = SMAASearchYUp(edgesTex, searchTex, offset[1].xy, offset[2].z);',
    '    coords.x = offset[0].x;',
    '    d.x = coords.y;',
    '    float e1 = texture(edgesTex, coords).g;',
    '    coords.y = SMAASearchYDown(edgesTex, searchTex, offset[1].zw, offset[2].w);',
    '    d.y = coords.y;',
    '    d = d / resolution.y - pixcoord.y;',
    '    vec2 sqrt_d = sqrt(abs(d));',
    '    coords.y -= 1.0 * resolution.y;',
    '    float e2 = SMAASampleLevelZeroOffset(edgesTex, coords, ivec2(0, 1)).g;',
    '    weights.ba = SMAAArea(areaTex, sqrt_d, e1, e2, float(subsampleIndices.x));',
    '  }',
    '  return weights;',
    '}',
    'void main() {',
    '  resolution = 1.0 / uInputSize.xy;',
    '  vec2 uv = vTextureCoord;',
    '  pixcoord = uv / resolution;',
    '  vec4 offset[3];',
    '  offset[0] = uv.xyxy + resolution.xyxy * vec4(-0.25, 0.125, 1.25, 0.125);',
    '  offset[1] = uv.xyxy + resolution.xyxy * vec4(-0.125, 0.25, -0.125, -1.25);',
    '  offset[2] = vec4(offset[0].xz, offset[1].yw) + vec4(-2.0, 2.0, -2.0, 2.0) * resolution.xxyy * float(SMAA_MAX_SEARCH_STEPS);',
    '  finalColor = SMAABlendingWeightCalculationPS(uv, pixcoord, offset, uTexture, uArea, uSearch, ivec4(0));',
    '}'
  ].join('\n');

  var BLEND_FRAGMENT = [
    'precision highp float;',
    'in vec2 vTextureCoord;',
    'out vec4 finalColor;',
    'uniform sampler2D uTexture;',
    'uniform sampler2D uColor;',
    'uniform vec4 uInputSize;',
    'void main() {',
    '  vec2 resolution = 1.0 / uInputSize.xy;',
    '  vec2 uv = vTextureCoord;',
    '  vec4 offset[2];',
    '  offset[0] = uv.xyxy + resolution.xyxy * vec4(-1.0, 0.0, 0.0, 1.0);',
    '  offset[1] = uv.xyxy + resolution.xyxy * vec4(1.0, 0.0, 0.0, -1.0);',
    '  vec4 a;',
    '  a.xz = texture(uTexture, uv).xz;',
    '  a.y = texture(uTexture, offset[1].zw).g;',
    '  a.w = texture(uTexture, offset[1].xy).a;',
    '  if (dot(a, vec4(1.0)) < 1e-5) {',
    '    finalColor = texture(uColor, uv);',
    '    return;',
    '  }',
    '  vec2 dir;',
    '  dir.x = a.a > a.b ? a.a : -a.b;',
    '  dir.y = a.g > a.r ? -a.g : a.r;',
    '  if (abs(dir.x) > abs(dir.y)) {',
    '    dir.y = 0.0;',
    '  } else {',
    '    dir.x = 0.0;',
    '  }',
    '  vec4 C = texture(uColor, uv);',
    '  vec2 uv2 = uv + sign(dir) * resolution;',
    '  vec4 Cop = texture(uColor, uv2);',
    '  float s = abs(dir.x) > abs(dir.y) ? abs(dir.x) : abs(dir.y);',
    '  finalColor = mix(C, Cop, s);',
    '}'
  ].join('\n');

  class SMAAFilter extends PIXI.Filter {
    constructor(areaTexture, searchTexture) {
      super({ compatibleRenderers: PIXI.RendererType.WEBGL, resources: {} });
      this.padding = 2;
      this._edges = PIXI.Filter.from({
        gl: { vertex: PIXI.defaultFilterVert, fragment: EDGE_FRAGMENT },
        resources: {}
      });
      this._weights = PIXI.Filter.from({
        gl: { vertex: PIXI.defaultFilterVert, fragment: WEIGHT_FRAGMENT },
        resources: {
          uArea: areaTexture.source,
          uSearch: searchTexture.source
        }
      });
      this._blend = PIXI.Filter.from({
        gl: { vertex: PIXI.defaultFilterVert, fragment: BLEND_FRAGMENT },
        resources: {
          uColor: PIXI.Texture.WHITE.source
        }
      });
    }

    apply(filterManager, input, output, clearMode) {
      var edges = PIXI.TexturePool.getSameSizeTexture(input);
      var weights = PIXI.TexturePool.getSameSizeTexture(input);
      try {
        this._edges.apply(filterManager, input, edges, true);
        this._weights.apply(filterManager, edges, weights, true);
        this._blend.resources.uColor = input.source;
        this._blend.blendMode = this.blendMode;
        this._blend.apply(filterManager, weights, output, clearMode);
      } finally {
        PIXI.TexturePool.returnTexture(edges);
        PIXI.TexturePool.returnTexture(weights);
      }
    }
  }

  window.VoxIME_SMAA = {
    create: async function () {
      var loaded = await Promise.all([
        PIXI.Assets.load('smaa/area.png'),
        PIXI.Assets.load('smaa/search.png')
      ]);
      loaded[0].source.scaleMode = 'linear';
      loaded[1].source.scaleMode = 'nearest';
      return new SMAAFilter(loaded[0], loaded[1]);
    }
  };
})();
