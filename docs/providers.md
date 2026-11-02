# VoxIME ASR 服务提供方配置指南

> 本文档由 `asr-providers` 维护，对应实现：`src/VoxIME.Core/Asr/**`。
> 所有适配器都实现 `VoxIME.Core.Abstractions.IAsrProvider`，通过 `AsrProviderRegistry.Descriptors/Presets`
> 暴露给设置界面。**本文中的接口细节以各厂商官方文档为准**；文中标注“需核对”的参数请在启用前按最新官方文档确认。

## 1. 总览

| 适配器（TypeId） | 界面名称 | 预设 | 鉴权 | 说明 |
|---|---|---|---|---|
| `openai-transcription` | OpenAI 兼容转录接口 | OpenAI Whisper API / 硅基流动 SenseVoiceSmall / LM Studio 本地 | `Authorization: Bearer`（可留空） | `POST {BaseUrl}/audio/transcriptions` multipart |
| `openai-audio-chat` | OpenAI 音频对话接口 | 小米 MiMo ASR / 阿里云 DashScope qwen3-asr-flash | Bearer 或 `api-key` 头 | `POST {BaseUrl}/chat/completions` + `input_audio` |
| `local-cli` | 本地命令行识别器 | whisper.cpp / FunASR / sherpa-onnx | 无 | 调用本机 CLI，占位符 `{wav}{out}{lang}{model}` |
| `tencent-sentence` | 腾讯云一句话识别 | 腾讯云 SentenceRecognition | SecretId + SecretKey | TC3-HMAC-SHA256，≤60 秒短音频 |
| `xfyun-iat` | 科大讯飞语音听写（流式） | 讯飞 IAT | APPID + APIKey + APISecret | WebSocket `wss://iat-api.xfyun.cn/v2/iat` |
| `mock` | Mock 离线演示 | Mock | 无 | 不联网，延迟 300-800ms 返回可预测文本 |

选择建议：

- 有网络 + 想省事：OpenAI Whisper / 硅基流动 SenseVoice / 小米 MiMo / DashScope。
- 完全离线 + 有显卡/CPU 余量：LM Studio、whisper.cpp、FunASR、sherpa-onnx。
- 国内合规采购：腾讯云、讯飞。
- 只想调 UI / 演示：Mock。

## 2. 通用配置字段

`ProviderProfile`（`src/VoxIME.Core/Configuration/AppConfig.cs`）字段说明：

| 字段 | 说明 |
|---|---|
| `typeId` | 适配器类型，必须等于上表 TypeId 之一 |
| `name` | 显示名称，用户可改 |
| `baseUrl` | 服务地址（见各节），本地 CLI / Mock 可留空 |
| `model` | 模型名（本地 CLI 中可用于 `{model}` 占位符） |
| `language` | 语言代码，默认 `zh`；`auto` 表示自动检测（按厂商支持情况省略或传 auto） |
| `prompt` | 识别提示词；OpenAI 转录接口放在 `prompt` 字段，音频对话接口放在 `messages` 的 system 消息 |
| `extraJson` | 厂商特定 JSON 对象，会合并进请求体/参数（见各节控制字段） |
| `timeoutSeconds` | 单次识别超时，默认 60（本地 CLI 建议 300+）。**HttpClient 统一超时无限，由这里控制** |
| `command` / `commandArguments` / `outputMode` / `outputFileTemplate` | 仅本地 CLI 使用 |
| `appId` | 讯飞的 APPID |
| `apiKey`（明文，直接落盘） | 各厂商 API Key / SecretKey / 讯飞 APIKey |
| `apiSecret`（内存）/ `apiSecretProtected` | 讯飞 APISecret |
| `secretId`（内存）/ `secretIdProtected` | 腾讯云 SecretId |

通用行为：

- 所有 HTTP 请求带 `User-Agent: VoxIME/0.1`。
- 非 2xx 统一抛 `AsrException`：`StatusCode` + 响应体前 1000 字符；超时、网络异常、取消也会映射成中文错误。
- 日志只记录地址/模型/音频长度/耗时，**不记录任何密钥**；密钥以明文保存在本地 `config.json`，请勿分享配置文件。
- 时间单位：`AsrResult.Latency` 是本次请求耗时，`AudioDuration` 是音频时长。

## 3. OpenAI 兼容转录接口（`openai-transcription`）

请求：`POST {BaseUrl}/audio/transcriptions`，`multipart/form-data`：

- `file` = `audio.wav`（`audio/wav`，16k 单声道 16bit PCM WAV）
- `model`、`language`（auto/空时省略）、`prompt`、`response_format=json`
- `extraJson` 中的标量字段会作为额外表单字段合并（可覆盖 `response_format`，例如 `{"response_format":"verbose_json","temperature":0}`）

响应解析 `text` 字段（`verbose_json` 同样含 `text`，并读取 `language`）；响应不是 JSON 时按纯文本兜底。

### 3.1 OpenAI Whisper API（预设）

| 项 | 值 |
|---|---|
| BaseUrl | `https://api.openai.com/v1` |
| Model | `whisper-1` |
| 密钥 | OpenAI API Key（Bearer） |
| 成本/限额 | 按音频时长计费（约 $0.006/分钟，以官网为准）；单文件 25MB 上限；需要海外网络 |
| 文档 | https://platform.openai.com/docs/api-reference/audio/createTranscription |

```json
{
  "id": "openai-whisper",
  "name": "OpenAI Whisper",
  "typeId": "openai-transcription",
  "baseUrl": "https://api.openai.com/v1",
  "model": "whisper-1",
  "language": "zh",
  "timeoutSeconds": 120,
  "enabled": true
}
```

### 3.2 硅基流动 SiliconFlow（预设）

| 项 | 值 |
|---|---|
| BaseUrl | `https://api.siliconflow.cn/v1` |
| Model | `FunAudioLLM/SenseVoiceSmall` |
| 密钥 | 硅基流动 API Key（Bearer） |
| 成本/限额 | 按音频时长计费，新用户有赠送额度；国内直连 |
| 文档 | https://docs.siliconflow.cn/cn/api-reference/audio/create-audio-transcriptions |

```json
{
  "id": "siliconflow-sensevoice",
  "name": "硅基流动 SenseVoice",
  "typeId": "openai-transcription",
  "baseUrl": "https://api.siliconflow.cn/v1",
  "model": "FunAudioLLM/SenseVoiceSmall",
  "language": "zh",
  "timeoutSeconds": 120,
  "enabled": true
}
```

### 3.3 LM Studio / 本地 OpenAI 兼容服务（预设）

| 项 | 值 |
|---|---|
| BaseUrl | `http://127.0.0.1:1234/v1`（LM Studio 默认端口；whisper.cpp server 视启动参数而定） |
| Model | `whisper-1`（LM Studio 里加载的本地模型标识，按实际改） |
| 密钥 | 本地服务一般不需要，留空即可 |
| 成本/限额 | 无费用；速度取决于本机 CPU/GPU |
| 文档 | https://lmstudio.ai/docs/app/api/endpoints/openai |

```json
{
  "id": "lmstudio-local",
  "name": "LM Studio 本地",
  "typeId": "openai-transcription",
  "baseUrl": "http://127.0.0.1:1234/v1",
  "model": "whisper-1",
  "language": "auto",
  "timeoutSeconds": 300,
  "enabled": true
}
```

> 提示：BaseUrl 可以直接填写完整接口地址（以 `/audio/transcriptions` 结尾），也可以只填到 `/v1`，
> 代码会先去掉末尾 `/` 再自动拼接。

## 4. OpenAI 音频对话接口（`openai-audio-chat`）

请求：`POST {BaseUrl}/chat/completions`，JSON：

```json
{
  "model": "mimo-v2.5-asr",
  "messages": [
    { "role": "user", "content": [
      { "type": "input_audio", "input_audio": { "data": "<base64>", "format": "wav" } }
    ]}
  ],
  "asr_options": { "language": "zh" }
}
```

- 语言为 `auto` 或空时省略 `asr_options.language`。
- `extraJson` 会**深度合并**进请求体（可加 `temperature`、`asr_options.enable_itn` 等）。
- 控制字段（不进入请求体）：
  - `"authHeader"`：`bearer`（默认，`Authorization: Bearer`）| `api-key`（`api-key:` 头）| `x-api-key`。
  - `"audioDataFormat"`：`base64`（默认，纯 base64 + `format` 字段）| `data-url`（`data:audio/wav;base64,...`，省略 `format`）。
- 配置了提示词时，请求会多一条 `{"role":"system","content":"..."}` 消息；用户提示词中的前台应用上下文由 VoxIME 自动附加。

### 4.1 小米 MiMo ASR（预设）

| 项 | 值 |
|---|---|
| BaseUrl | `https://api.xiaomimimo.com/v1` |
| Model | `mimo-v2.5-asr` |
| 密钥 | MiMo API Key；默认 `Authorization: Bearer sk-...`，也支持 `api-key: sk-...`（ExtraJson 设置 `"authHeader":"api-key"`） |
| 音频 | mp3 / wav；纯 base64 时必须带 `format`，data URL 时可省略 |
| 成本/限额 | 以小米开放平台计费与配额为准 |
| 文档 | https://platform.xiaomimimo.com/docs/en-US/usage-guide/Speech-Recognition （本机 `https://mimo.mi.com/static/docs/api/audio/Speech-Recognition.md` 可访问） |

```json
{
  "id": "mimo-asr",
  "name": "小米 MiMo ASR",
  "typeId": "openai-audio-chat",
  "baseUrl": "https://api.xiaomimimo.com/v1",
  "model": "mimo-v2.5-asr",
  "language": "zh",
  "timeoutSeconds": 120,
  "enabled": true
}
```

### 4.2 阿里云 DashScope / qwen3-asr-flash（预设）

| 项 | 值 |
|---|---|
| BaseUrl | `https://dashscope.aliyuncs.com/compatible-mode/v1`（官方已建议迁移到 `{WorkspaceId}.cn-beijing.maas.aliyuncs.com/compatible-mode/v1`） |
| Model | `qwen3-asr-flash` |
| 密钥 | 百炼 API Key（Bearer） |
| 音频 | **必须** `data:<mime>;base64,<data>` 的 Data URI（预设已带 `"audioDataFormat":"data-url"`）；支持 wav/mp3 等，编码后 ≤10MB |
| 成本/限额 | 按音频时长计费；美国地域不支持 OpenAI 兼容模式 |
| 文档 | https://help.aliyun.com/zh/model-studio/qwen-asr-api-reference |

```json
{
  "id": "dashscope-qwen-asr",
  "name": "DashScope qwen3-asr-flash",
  "typeId": "openai-audio-chat",
  "baseUrl": "https://dashscope.aliyuncs.com/compatible-mode/v1",
  "model": "qwen3-asr-flash",
  "language": "zh",
  "extraJson": "{\"audioDataFormat\":\"data-url\"}",
  "timeoutSeconds": 120,
  "enabled": true
}
```

## 5. 腾讯云一句话识别（`tencent-sentence`）

| 项 | 值 |
|---|---|
| BaseUrl | `https://asr.tencentcloudapi.com`（Host 参与签名，一般不要改） |
| Action / Version | `SentenceRecognition` / `2019-06-14` |
| Model | 留空；引擎由语言映射：`zh→16k_zh`、`en→16k_en`、`yue→16k_yue`、`ja→16k_ja` 等，也可在 ExtraJson 直接写 `EngSerViceType` |
| 密钥 | `secretId` = SecretId，`apiKey` = SecretKey |
| ExtraJson | `region`（默认 `ap-guangzhou`）、`FilterDirty`/`FilterModal`/`ConvertNumMode`/`WordInfo`/`HotwordList` 等合并进请求体 |
| 音频 | wav（16k 更佳），**≤60 秒、Base64 后 ≤3MB**；请求体含 `Data`/`DataLen` |
| 成本/限额 | 默认 30 次/秒；按调用量/资源包计费（以官网为准） |
| 文档 | https://cloud.tencent.com/document/api/1093/35646 |

签名：TC3-HMAC-SHA256（`Service=asr`、`SignedHeaders=content-type;host`），日期/时间戳取自本机 UTC 时钟
（偏差过大需校准系统时间）；单测通过注入时钟验证签名结构。

```json
{
  "id": "tencent-asr",
  "name": "腾讯云一句话识别",
  "typeId": "tencent-sentence",
  "baseUrl": "https://asr.tencentcloudapi.com",
  "language": "zh",
  "extraJson": "{\"region\":\"ap-guangzhou\"}",
  "timeoutSeconds": 60,
  "enabled": true
}
```

## 6. 科大讯飞语音听写（`xfyun-iat`）

| 项 | 值 |
|---|---|
| BaseUrl | `wss://iat-api.xfyun.cn/v2/iat` |
| 密钥 | `appId` = APPID，`apiKey` = APIKey，`apiSecret` = APISecret（三者都要） |
| 协议 | Query 携带 `authorization/date/host`（HMAC-SHA256→Base64），音频 16k/8k 单声道 PCM 按 **1280 字节/40ms** 分帧，首帧 `status=0` 带 common/business，中间帧 `status=1`，末帧 `status=2` |
| business | 默认 `language=zh_cn`、`domain=iat`、`accent=mandarin`；ExtraJson 可覆盖（如 `{"eos":3000,"ptt":1}`，也支持 `{"business":{...}}` 嵌套）。`eos` 与旧版 `vad_eos` 会同步发送以兼容不同引擎版本 |
| 结果 | 解析 `data.result.ws[].cw[0].w` 按 `sn` 拼装；开启动态修正（`dwa=wpgs`）时支持 `pgs=apd/rpl` 与替换范围 `rg`（兼容旧字段名 `rl`） |
| 限额 | 单会话 ≤60 秒，累计 10 秒未发送数据会被断开；需在控制台开通“语音听写（流式版）”并配置 IP 白名单 |
| 成本/限额 | 按调用量/套餐计费（以讯飞官网为准） |
| 文档 | https://www.xfyun.cn/doc/asr/voicedictation/API.html |

> 注意：BaseUrl 仅支持 `ws://` / `wss://`；单次识别总时长受 `timeoutSeconds` 与讯飞 60 秒会话上限双重限制。

```json
{
  "id": "xfyun-iat",
  "name": "讯飞语音听写",
  "typeId": "xfyun-iat",
  "baseUrl": "wss://iat-api.xfyun.cn/v2/iat",
  "language": "zh",
  "timeoutSeconds": 60,
  "enabled": true
}
```

## 7. 本地命令行识别器（`local-cli`）

通用机制：

1. 把音频写成临时 `audio.wav`，并把 `{wav}` 替换为绝对路径、`{out}` 为去掉扩展名的输出前缀、`{lang}` 为语言、`{model}` 为 profile.Model。
2. 参数按空白拆分（支持双引号/单引号包裹含空格的参数）。
3. `outputMode` 决定取结果方式：
   - `stdout`：取全部标准输出并 trim；
   - `file`：读取 `outputFileTemplate`（默认 `{out}.txt`）指定的文件；
   - `json`：解析 stdout（为空时读输出文件），按 ExtraJson `{"textPath":"result.text"}` 指定的点号路径取文本（默认 `text`，支持数组下标如 `result.0.text`）。
4. 超时用 `timeoutSeconds`，取消/超时都会杀掉整个进程树并清理临时目录；非零退出码、空输出、文件缺失都会带 stderr 摘要抛 `AsrException`。
5. 工作目录沿用应用当前目录（便于 CLI 使用相对模型路径），临时 WAV 始终传绝对路径。

三个预设的模板（**FunASR / sherpa-onnx 参数需按你本地版本核对**）：

| 预设 | Command | CommandArguments | OutputMode |
|---|---|---|---|
| whisper.cpp | `whisper-cli` | `-m {model} -f {wav} -l {lang} -nt -otxt -of {out}` | `file`（`{out}.txt`） |
| FunASR CLI | `funasr` | `++model={model} ++wav={wav} ++output_dir={out}` | `stdout` |
| sherpa-onnx | `sherpa-onnx-offline` | `--tokens={model}/tokens.txt --paraformer={model}/model.onnx --wav={wav}` | `stdout` |

- `Command` 可以是绝对路径（`C:\tools\whisper-cli.exe`），也可以已在 PATH 中。
- whisper.cpp 需要把 `model` 改成实际 ggml 模型路径（如 `models/ggml-base.bin`），或把模型目录加入 PATH/工作目录。
- FunASR / sherpa-onnx 的 stdout 通常包含日志，必要时改用 `file`/`json` 模式并配置 `outputFileTemplate` / `textPath`；参数模板以本地版本 `--help` 为准。

```json
{
  "id": "whisper-cpp-local",
  "name": "whisper.cpp 本地",
  "typeId": "local-cli",
  "model": "models/ggml-base.bin",
  "language": "zh",
  "command": "whisper-cli",
  "commandArguments": "-m {model} -f {wav} -l {lang} -nt -otxt -of {out}",
  "outputMode": "file",
  "outputFileTemplate": "{out}.txt",
  "timeoutSeconds": 300,
  "enabled": true
}
```

## 8. Mock（`mock`）

- 不联网、不调用任何 API；延迟 300-800ms；返回文本：
  `你好，这是 VoxIME Mock 识别结果，音频约 {x.x} 秒。`
- 可用 `extraJson` 的 `{"text":"..."}` 覆盖返回文本（调用级 ExtraJson 优先）。
- 适合 UI 联调、演示与自动化测试。

```json
{
  "id": "mock",
  "name": "Mock 离线演示",
  "typeId": "mock",
  "model": "mock",
  "language": "zh",
  "timeoutSeconds": 30,
  "enabled": true
}
```

## 9. 常见问题

| 现象 | 排查 |
|---|---|
| 创建服务后点识别无反应 / 提示“创建 ASR 识别器失败” | 设置页底部服务列表里看日志；多为 BaseUrl/Model/密钥缺失（错误信息会中文指出缺失项） |
| HTTP 401/403 | 密钥错误、未开通服务、IP 白名单（讯飞/腾讯）、地域不对（DashScope 美国地域不支持兼容模式） |
| HTTP 429 | 触发限流/额度不足，检查厂商控制台 |
| 超时（`AsrException` 文案带“超时”） | 调大 `timeoutSeconds`；腾讯/讯飞单请求/单会话本来就有 60 秒上限 |
| 本地 CLI 报“退出码 / 没有输出识别结果 / 没有生成输出文件” | 看异常中的 stderr 摘要；用命令行手动跑一遍确认参数模板与输出模式 |
| 识别文本为空 | 厂商返回空文本时会保留空串；Xfyun/CLI 会按“无结果”抛异常 |

## 10. 免责声明与待核对项

- 本文档是 VoxIME 的集成说明，**不代表任何厂商官方文档**；接口地址、模型名、计费与限额可能随时变化，
  请在启用前对照官方文档核对。
- 以下内容实现时按公开 API 常见形态编写，**需要按最新官方文档核对**：
  - 硅基流动 SenseVoice 模型名（当前 `FunAudioLLM/SenseVoiceSmall`，官方可能推荐新模型）；
  - LM Studio / whisper.cpp server 的模型标识与返回格式；
  - FunASR CLI、sherpa-onnx-offline 的参数模板与输出格式（不同版本差异较大）；
  - 小米 MiMo 的鉴权头与 `asr_options` 支持范围（以 MiMo 官方文档为准）；
  - 阿里云 DashScope 专属业务空间域名迁移（`{WorkspaceId}.cn-beijing.maas.aliyuncs.com`）；
  - 腾讯云 Region 取值与引擎类型列表、讯飞 `eos`/`vad_eos` 字段的引擎版本兼容性。
- 所有厂商的密钥仅保存在本机（`config.json` 明文），VoxIME 不会上传、也不会写入日志；请勿把密钥提交到
  版本库或截图分享。
