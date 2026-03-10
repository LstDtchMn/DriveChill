/**
 * AudioMeter — Web Audio API utility for measuring A-weighted dB from microphone.
 *
 * Usage:
 *   const meter = new AudioMeter();
 *   await meter.start();
 *   const db = meter.getDB();
 *   meter.stop();
 */

// A-weighting coefficients (approximate, per-bin)
// We apply a simplified A-weighting curve using frequency bins.
function aWeightDB(freqHz: number): number {
  if (freqHz <= 0) return 0;
  const f2 = freqHz * freqHz;
  const f4 = f2 * f2;
  // IEC 61672 A-weighting formula
  const numerator = 12194 ** 2 * f4;
  const denominator =
    (f2 + 20.6 ** 2) *
    Math.sqrt((f2 + 107.7 ** 2) * (f2 + 737.9 ** 2)) *
    (f2 + 12194 ** 2);
  const ra = numerator / denominator;
  return 20 * Math.log10(ra) + 2.0;
}

export class AudioMeter {
  private ctx: AudioContext | null = null;
  private analyser: AnalyserNode | null = null;
  private stream: MediaStream | null = null;
  private source: MediaStreamAudioSourceNode | null = null;
  private fftSize = 2048;

  /**
   * Request microphone access, create AudioContext, wire up AnalyserNode.
   * Throws if microphone permission is denied.
   */
  async start(): Promise<void> {
    if (this.ctx) {
      // Already started — resume if suspended
      if (this.ctx.state === 'suspended') {
        await this.ctx.resume();
      }
      return;
    }

    this.stream = await navigator.mediaDevices.getUserMedia({ audio: true, video: false });
    this.ctx = new AudioContext();
    this.analyser = this.ctx.createAnalyser();
    this.analyser.fftSize = this.fftSize;
    this.analyser.smoothingTimeConstant = 0.3;

    this.source = this.ctx.createMediaStreamSource(this.stream);
    this.source.connect(this.analyser);
    // Do NOT connect to destination — we don't want to hear ourselves
  }

  /**
   * Compute an approximate A-weighted dB(A) reading from the current FFT frame.
   * Returns -Infinity if the meter has not been started.
   */
  getDB(): number {
    if (!this.analyser || !this.ctx) return -Infinity;

    const bufferLength = this.analyser.frequencyBinCount;
    const dataArray = new Float32Array(bufferLength);
    this.analyser.getFloatFrequencyData(dataArray);

    const binWidth = this.ctx.sampleRate / this.fftSize;

    let weightedPower = 0;
    let totalWeight = 0;

    for (let i = 1; i < bufferLength; i++) {
      const freqHz = i * binWidth;
      const linearAmplitude = Math.pow(10, dataArray[i] / 20); // dBFS → linear
      const power = linearAmplitude * linearAmplitude;
      const weight = Math.pow(10, aWeightDB(freqHz) / 10); // A-weight factor
      weightedPower += power * weight;
      totalWeight += weight;
    }

    if (weightedPower <= 0 || totalWeight <= 0) return -Infinity;

    // Convert back to dB, normalised to 0 dBFS reference
    const db = 10 * Math.log10(weightedPower / totalWeight);
    return db;
  }

  /**
   * Collect multiple dB samples over the given duration and return the median.
   * @param durationMs  Measurement window in milliseconds (default 3000)
   * @param intervalMs  Sample interval in milliseconds (default 100)
   */
  async measureMedianDB(durationMs = 3000, intervalMs = 100): Promise<number> {
    const samples: number[] = [];
    const end = Date.now() + durationMs;

    while (Date.now() < end) {
      const sample = this.getDB();
      if (isFinite(sample)) {
        samples.push(sample);
      }
      await new Promise<void>((resolve) => setTimeout(resolve, intervalMs));
    }

    if (samples.length === 0) return -Infinity;

    samples.sort((a, b) => a - b);
    const mid = Math.floor(samples.length / 2);
    return samples.length % 2 === 0
      ? (samples[mid - 1] + samples[mid]) / 2
      : samples[mid];
  }

  /** Release all resources and close the AudioContext. */
  stop(): void {
    if (this.source) {
      try { this.source.disconnect(); } catch { /* ignore */ }
      this.source = null;
    }
    if (this.stream) {
      this.stream.getTracks().forEach((t) => t.stop());
      this.stream = null;
    }
    if (this.ctx) {
      try { this.ctx.close(); } catch { /* ignore */ }
      this.ctx = null;
    }
    this.analyser = null;
  }
}
