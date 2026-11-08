use std::f32::consts::PI;
use std::slice;

const MAX_CHANNELS: usize = 16;

/// 定长 radix-2 FFT（512 点），与 C# 侧 FftN 的行为一致。
struct Fft {
    n: usize,
    bit_reverse: Vec<usize>,
    cos_table: Vec<f32>,
    sin_table: Vec<f32>,
}

impl Fft {
    fn new(n: usize) -> Self {
        let mut bit_reverse = vec![0usize; n];
        let bits = n.trailing_zeros() as usize;

        for i in 0..n {
            let mut reversed = 0usize;
            for b in 0..bits {
                if (i >> b) & 1 == 1 {
                    reversed |= 1 << (bits - 1 - b);
                }
            }
            bit_reverse[i] = reversed;
        }

        let half = n / 2;
        let mut cos_table = vec![0f32; half];
        let mut sin_table = vec![0f32; half];

        for i in 0..half {
            let angle = -2.0 * PI * i as f32 / n as f32;
            cos_table[i] = angle.cos();
            sin_table[i] = angle.sin();
        }

        Self {
            n,
            bit_reverse,
            cos_table,
            sin_table,
        }
    }

    fn transform(&self, re: &mut [f32], im: &mut [f32], inverse: bool) {
        let n = self.n;

        for i in 0..n {
            let j = self.bit_reverse[i];
            if j > i {
                re.swap(i, j);
                im.swap(i, j);
            }
        }

        let mut len = 2usize;
        while len <= n {
            let half = len / 2;
            let step = n / len;

            for start in (0..n).step_by(len) {
                for k in 0..half {
                    let twiddle = k * step;
                    let wr = self.cos_table[twiddle];
                    let wi = if inverse {
                        -self.sin_table[twiddle]
                    } else {
                        self.sin_table[twiddle]
                    };

                    let even = start + k;
                    let odd = even + half;

                    let tr = re[odd] * wr - im[odd] * wi;
                    let ti = re[odd] * wi + im[odd] * wr;

                    re[odd] = re[even] - tr;
                    im[odd] = im[even] - ti;
                    re[even] += tr;
                    im[even] += ti;
                }
            }

            len <<= 1;
        }

        if inverse {
            let scale = 1.0 / n as f32;
            for i in 0..n {
                re[i] *= scale;
                im[i] *= scale;
            }
        }
    }
}

struct SpeakerConvolver {
    channel: usize,
    gain: f32,
    block_size: usize,
    fft_size: usize,
    overlap_len: usize,
    fft: Fft,

    ir_left_re: Vec<f32>,
    ir_left_im: Vec<f32>,
    ir_right_re: Vec<f32>,
    ir_right_im: Vec<f32>,

    input_re: Vec<f32>,
    input_im: Vec<f32>,
    accum_re: Vec<f32>,
    accum_im: Vec<f32>,

    overlap_left: Vec<f32>,
    overlap_right: Vec<f32>,
}

impl SpeakerConvolver {
    fn new(
        channel: usize,
        gain: f32,
        left: &[f32],
        right: &[f32],
        block_size: usize,
    ) -> Self {
        let ir_len = left.len().max(right.len());
        let mut fft_size = 1usize;
        while fft_size < block_size + ir_len - 1 {
            fft_size <<= 1;
        }

        let overlap_len = fft_size - block_size;
        let fft = Fft::new(fft_size);

        let mut ir_left_re = vec![0f32; fft_size];
        let mut ir_left_im = vec![0f32; fft_size];
        let mut ir_right_re = vec![0f32; fft_size];
        let mut ir_right_im = vec![0f32; fft_size];

        for i in 0..left.len().min(fft_size) {
            ir_left_re[i] = left[i];
        }
        for i in 0..right.len().min(fft_size) {
            ir_right_re[i] = right[i];
        }

        fft.transform(&mut ir_left_re, &mut ir_left_im, false);
        fft.transform(&mut ir_right_re, &mut ir_right_im, false);

        Self {
            channel,
            gain,
            block_size,
            fft_size,
            overlap_len,
            fft,
            ir_left_re,
            ir_left_im,
            ir_right_re,
            ir_right_im,
            input_re: vec![0f32; fft_size],
            input_im: vec![0f32; fft_size],
            accum_re: vec![0f32; fft_size],
            accum_im: vec![0f32; fft_size],
            overlap_left: vec![0f32; overlap_len],
            overlap_right: vec![0f32; overlap_len],
        }
    }

    fn process(&mut self, input: &[f32], channels: usize, mix: &mut [f32]) {
        for value in self.input_re.iter_mut() {
            *value = 0.0;
        }
        for value in self.input_im.iter_mut() {
            *value = 0.0;
        }

        for i in 0..self.block_size {
            self.input_re[i] = input[i * channels + self.channel] * self.gain;
        }

        self.fft
            .transform(&mut self.input_re, &mut self.input_im, false);

        self.accumulate_ear(false, 0, mix);
        self.accumulate_ear(true, 1, mix);
    }

    fn accumulate_ear(&mut self, right_ear: bool, ear: usize, mix: &mut [f32]) {
        let (ir_re, ir_im) = if right_ear {
            (&self.ir_right_re, &self.ir_right_im)
        } else {
            (&self.ir_left_re, &self.ir_left_im)
        };

        for i in 0..self.fft_size {
            self.accum_re[i] = self.input_re[i] * ir_re[i] - self.input_im[i] * ir_im[i];
            self.accum_im[i] = self.input_re[i] * ir_im[i] + self.input_im[i] * ir_re[i];
        }

        self.fft
            .transform(&mut self.accum_re, &mut self.accum_im, true);

        let overlap = if right_ear {
            &mut self.overlap_right
        } else {
            &mut self.overlap_left
        };

        for i in 0..self.block_size {
            mix[i * 2 + ear] += self.accum_re[i] + overlap[i];
        }

        for i in 0..self.overlap_len {
            let mut tail = self.accum_re[self.block_size + i];
            if i + self.block_size < self.overlap_len {
                tail += overlap[i + self.block_size];
            }
            overlap[i] = tail;
        }
    }
}

struct Engine {
    input_channels: usize,
    block_size: usize,
    speakers: Vec<SpeakerConvolver>,
}

#[no_mangle]
pub extern "C" fn sk02_hrtf_create(sample_rate: u32, block_size: u32) -> *mut Engine {
    let _ = sample_rate;

    if block_size == 0 || block_size > 4096 {
        return std::ptr::null_mut();
    }

    Box::into_raw(Box::new(Engine {
        input_channels: 0,
        block_size: block_size as usize,
        speakers: Vec::new(),
    }))
}

/// ir 布局：speaker_count * 2 * taps，按 [speaker][ear][tap] 排列；
/// gains / channel_indices 每个扬声器一个。input_channels 是原始输入声道数（可能包含 LFE）。
#[no_mangle]
pub unsafe extern "C" fn sk02_hrtf_configure(
    engine: *mut Engine,
    input_channels: u32,
    speaker_count: u32,
    taps: u32,
    irs: *const f32,
    gains: *const f32,
    channel_indices: *const u32,
) -> i32 {
    if engine.is_null() || irs.is_null() || gains.is_null() || channel_indices.is_null() {
        return -1;
    }

    let engine = &mut *engine;
    let input_channels = input_channels as usize;
    let speaker_count = speaker_count as usize;
    let taps = taps as usize;

    if input_channels == 0 || input_channels > MAX_CHANNELS ||
       speaker_count == 0 || speaker_count > MAX_CHANNELS ||
       taps == 0 || taps > 4096 {
        return -1;
    }

    let total = speaker_count * 2 * taps;
    let ir_slice = slice::from_raw_parts(irs, total);
    let gain_slice = slice::from_raw_parts(gains, speaker_count);
    let channel_slice = slice::from_raw_parts(channel_indices, speaker_count);

    let mut speakers = Vec::with_capacity(speaker_count);

    for ch in 0..speaker_count {
        let left_start = ch * 2 * taps;
        let right_start = left_start + taps;
        let left = &ir_slice[left_start..left_start + taps];
        let right = &ir_slice[right_start..right_start + taps];

        speakers.push(SpeakerConvolver::new(
            channel_slice[ch] as usize,
            gain_slice[ch],
            left,
            right,
            engine.block_size,
        ));
    }

    engine.input_channels = input_channels;
    engine.speakers = speakers;
    0
}

/// 处理一个 block_size 块，输出交错立体声 wet（左右各 block_size）。
#[no_mangle]
pub unsafe extern "C" fn sk02_hrtf_process(
    engine: *mut Engine,
    input: *const f32,
    output: *mut f32,
) -> i32 {
    if engine.is_null() || input.is_null() || output.is_null() {
        return -1;
    }

    let engine = &mut *engine;
    if engine.speakers.is_empty() {
        return -1;
    }

    let frames = engine.block_size;
    let channels = engine.input_channels;
    let input = slice::from_raw_parts(input, frames * channels);
    let output = slice::from_raw_parts_mut(output, frames * 2);

    for value in output.iter_mut() {
        *value = 0.0;
    }

    for speaker in engine.speakers.iter_mut() {
        speaker.process(input, channels, output);
    }

    0
}

#[no_mangle]
pub unsafe extern "C" fn sk02_hrtf_destroy(engine: *mut Engine) {
    if !engine.is_null() {
        drop(Box::from_raw(engine));
    }
}
