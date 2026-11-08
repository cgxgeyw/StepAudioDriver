# Rust DSP 核心

## 结构

- `rust/step-dsp/`：Rust `cdylib`，实现 8 声道 HRTF 卷积（256-tap HRIR、128 帧分块、512 点 FFT、overlap-add）
- `src/Step.Core/Audio/HRTF/NativeHrtf.cs`：P/Invoke 封装，DLL 不存在时自动回退 C#
- `src/Step.UI/native/step_dsp.dll`：构建产物，随 UI 输出复制到运行目录
- C# 仍负责 WASAPI 捕获/输出、EQ、音量/限幅、干/湿混合和 LFE

## 接口

```c
void* step_hrtf_create(uint32_t sample_rate, uint32_t block_size);
int   step_hrtf_configure(void* engine, uint32_t input_channels, uint32_t speaker_count,
                          uint32_t taps, const float* irs, const float* gains,
                          const uint32_t* channel_indices);
int   step_hrtf_process(void* engine, const float* input, float* output);
void  step_hrtf_destroy(void* engine);
```

- `input_channels` 是原始输入声道数（8 声道时包含 LFE）；`speaker_count` 是实际参与卷积的扬声器数（7.1 时 7 个，LFE 不参与）
- `channel_indices` 是每个扬声器对应的原始输入声道序号（例如 0,1,2,4,5,6,7），不能直接用 0..speaker_count-1
- `irs` 布局：`[speaker][ear][tap]`
- `input`：`block_size * input_channels` 交错采样
- `output`：`block_size * 2` 交错立体声 wet

> 早期版本把 `speaker_count` 当成了 `input_channels`，导致 7.1 下 LFE 被当成扬声器、SR 丢失、声道错位（方向错乱 + 爆音）；现已拆成两个参数。

## 实测（本机，8 声道 / 256 taps / 128 帧）

| 实现 | 每 10ms 块耗时 | p95 | GC |
|---|---|---|---|
| C# `SpeakerConvolver` | 2.27 ms | 2.54 ms | 0 |
| Rust 核心 | **0.46 ms** | 0.51 ms | 0 |

音质参数不变：ITD/ILD 与 C# 实现一致（±60° 时 ITD 24 samples、ILD 8.4 dB）。

## 构建

```powershell
cd rust/step-dsp
cargo build --release
copy target/release/step_dsp.dll ..\..\src\Step.UI\native\step_dsp.dll
```

UI 项目会把 `native/step_dsp.dll` 复制到输出目录；找不到 DLL 时自动走 C# 回退，不影响功能。
