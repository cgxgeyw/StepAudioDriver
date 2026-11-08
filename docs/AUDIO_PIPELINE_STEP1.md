# 步骤 1：音频链路已接通

**日期**：2025-11（本次改动）
**范围**：把 `CABLE Input → DSP → SK02` 这条主链路真正接起来，让「能听到声音」成立。

## 现在能做什么

```
游戏 / 播放器
   ↓  输出设备设为 "CABLE Input (VB-Audio Virtual Cable)"
VB-CABLE
   ↓  WASAPI loopback 捕获（48kHz / 32bit float / 立体声）
声道下混（1/2/4/6/8/16ch → 立体声，留 0.5 余量，不硬削）
   ↓
HRTF 双耳空间化（干/湿混合，内部不再削波）
   ↓
10 段参数化 EQ（RBJ peaking biquad，每声道独立状态）
   ↓
音量 + 2ms 前瞻峰值限幅（阈值 0.88，150ms 释放）
   ↓
WASAPI 输出（独占 → 共享 → 降级格式，自动回退）
   ↓
耳机 (Fosi Audio SK02)
```

界面顶部新增「▶ 启动音频 / ■ 停止音频」，右侧显示当前链路与实时性能：

```
运行中 · CABLE Input → 耳机     延迟 0.6 ms · CPU 0% · 缓冲 28%
```

状态栏悬停可以看到完整链路（含采样率、位深、声道数）。

## 本次改动

| 文件 | 改动 |
|---|---|
| `src/Step.Core/Audio/LoopbackCaptureEngine.cs` | 只做捕获与 byte→float 转换，删除内部第二次 DSP 调用；修正 24-bit 符号扩展；只转换完整帧；暴露设备名 |
| `src/Step.Core/Audio/RealTimeAudioProcessor.cs` | 构造参数改为输出设备；统一立体声输出；任意声道下混；HRTF 阶段默认旁路（`HrtfEnabled=false`）；错误只上报不抛出；增加 `InputPeak`、帧计数 |
| `src/Step.Core/Audio/WASAPIOutputEngine.cs` | 输出格式由调用方给定并支持降级；缓冲从 2s 改为 100ms；按实际格式转换（16/24/32bit、32f）；输出设备解析跳过虚拟线缆；新增溢出丢弃计数 |
| `src/Step.Core/Audio/DSP/EqualizerEngine.cs` | 每声道独立滤波状态；支持自定义中心频率/Q；系数快照原子替换（UI 与音频线程安全）；数值异常保护 |
| `src/Step.Core/Audio/WaveFormatExtensions.cs` | 处理 WAVE_FORMAT_EXTENSIBLE 的 float 判断（否则 32f 会被当 PCM） |
| `src/Step.Core/Performance/PerformanceMonitor.cs` | CPU 采样改为差分计算，移出音频线程；延迟队列加锁 |
| `src/Step.UI/MainWindow.xaml(.cs)` | 顶部状态栏与启动/停止按钮；EQ/HRTF/设备/旋钮全部接到处理器；移除每次切预设的模态弹框；关闭窗口时正确释放设备 |

## 峰值处理（避免鼓点破音）

- **下混留余量**：多声道下混按 0.5 缩放，不再在 `DownmixToStereo` 里 `Math.Clamp(±1)`；削波交给最终限幅器
- **HRTF 不再内部硬削**：湿/干相加后直接输出，避免低 HRTF 强度（干声占比高）时在空间化阶段先削一次
- **2ms 前瞻限幅**：阈值 0.88，约 2ms 压下来、150ms 释放；替代原来的 `tanh` 波形饱和，鼓点瞬态不再被“压扁”
- **输出优先独占**：先尝试 WASAPI 独占；设备明确不支持独占时（如 N9V2 dongle）自动回退共享；设备被其它程序占用则不回退，避免变成混音
- **EQ 预衰减**：按当前预设最大正增益自动计算 preamp（最大增益 +1 dB 余量），先衰减再进 EQ，避免正增益把峰值推过 1 后让限幅器持续压缩（这是“所有预设都破音”的根因）
- **缓冲档位**：稳定 100ms / 平衡 60ms / 低延迟 40ms（捕获与输出同时生效），默认 100ms；捕获改用 `BufferedLoopbackCapture`（继承 `WasapiCapture` 加 Loopback 标志），不再反射改私有缓冲，40/60/100ms 均已实测可捕获
- 之前试过 5ms/15ms，8 声道下会 underrun，表现为所有预设破音；稳定优先
- **实测**：2.0 幅度正弦 / 3.0 幅度鼓点脉冲，输出峰值 0.896 / 0.892，没有样本超过 0.9，也没有硬削
- 代价是链路多 2ms 延迟（总延迟约 6–9ms，取决于声道数）

## 实测结果（本机，21:4x）

- `dotnet build`：成功，0 错误
- 端到端链路（脚本向 CABLE Input 播 440Hz 测试音，捕获后输出到 SK02）：
  - 捕获峰值 0.0005（与测试音一致），说明捕获与下混正确
  - 捕获帧率 = 写出帧率（≈48,300 f/s），缓冲 9–66% 波动，**30 秒内 0 次溢出丢弃**
  - 单块处理耗时 0.55–0.85 ms（块长 50 ms，约 1.5% 实时占用）
- DSP 离线校验：EQ 2kHz +6dB 实测 **6.00 dB**；音量 0.5 实测 **0.500**
- UI 自动化：点击启动 → 状态显示「运行中 · CABLE Input → 耳机」→ 性能行每秒刷新 → 停止 → 再次启动/停止均正常

## 手动验证

1. 播放器/游戏的输出设备设为 **CABLE Input (VB-Audio Virtual Cable)**
2. 启动 `src/Step.UI/bin/Debug/net8.0-windows/StepAudioDriver.exe`
3. 点击右上角 **▶ 启动音频**，状态应变绿并显示「运行中」
4. 播放音乐，耳机应能听到；拖动 EQ 滑块应立即听出变化
5. 点击 **■ 停止音频** 恢复直通（游戏仍走 CABLE，但不再经过本程序）

## 已知限制（下一步）

1. ~~**HRTF 未接入**~~ → 已在步骤 2 接入，见 [HRTF_SPATIALIZER_STEP2.md](HRTF_SPATIALIZER_STEP2.md)。目前 HRIR 为参数化模型，不是实测数据。
2. **延迟**：当前约 30–70 ms（100 ms 缓冲区 + WASAPI 共享缓冲），FPS 目标需要降到 20 ms 级；需要更小的缓冲与时钟漂移补偿。
3. **无单元测试**：本次验证是脚本级冒烟测试，尚未落地到 xunit。
4. **`Step.Service` 仍为空**，`.bak` 文件、CSCore 无用依赖、乱码注释等遗留问题未清理。
