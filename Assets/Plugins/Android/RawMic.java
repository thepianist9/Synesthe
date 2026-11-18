package com.metastarter.week1bench;

import android.content.Context;
import android.media.AudioDeviceInfo;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.audiofx.AcousticEchoCanceler;
import android.media.audiofx.AutomaticGainControl;
import android.media.audiofx.NoiseSuppressor;
import android.os.Build;
import android.util.Log;

/**
 * Raw microphone capture for the Week 1 bench.
 *
 * Unity's Microphone class records with VOICE_RECOGNITION, which Quest processes for the
 * wearer's voice and largely removes other people. This class lets the bench choose the
 * Android audio source (UNPROCESSED = 9, MIC = 1, CAMCORDER = 5, VOICE_RECOGNITION = 6),
 * the channel layout (1 = mono, 2 = stereo L/R) and disables any attached voice effects.
 *
 * Unity polls {@link #poll()} each frame; it returns interleaved float samples captured
 * since the last call (48 kHz).
 *
 * External microphones (USB-C mics, wireless clip-on receivers, Bluetooth headsets) are listed by
 * {@link #listInputs(Context)} and chosen with the deviceId argument of {@link #start(int, int, int)}.
 */
public class RawMic {
    private static final String TAG = "Week1RawMic";
    private static final int RATE = 48000;

    private AudioRecord record;
    private Thread thread;
    private volatile boolean running;
    private final Object lock = new Object();
    private float[] ring = new float[RATE * 2 * 4]; // 2 s of up to 4 channels
    private int ringCount = 0;
    private int channels = 1;
    private String status = "idle";
    private static Context context;
    private AudioManager commManager; // set while a Bluetooth headset is routed as communication device

    /** Call once with the Unity activity before start() with a deviceId or listInputs(). */
    public static void init(Context ctx) { context = ctx.getApplicationContext(); }

    /**
     * Audio input devices, one per line: id \t type \t product name \t address.
     * Types are AudioDeviceInfo.TYPE_* (15 built-in mic, 11/12/22 USB, 7/26 Bluetooth, 3 wired headset).
     */
    public static String listInputs(Context ctx) {
        AudioManager am = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
        StringBuilder sb = new StringBuilder();
        for (AudioDeviceInfo d : am.getDevices(AudioManager.GET_DEVICES_INPUTS)) {
            CharSequence name = d.getProductName();
            sb.append(d.getId()).append('\t').append(d.getType()).append('\t')
              .append(name != null ? name.toString().replace('\t', ' ').replace('\n', ' ') : "").append('\t')
              .append(d.getAddress() != null ? d.getAddress() : "").append('\n');
        }
        return sb.toString();
    }

    private static AudioDeviceInfo findInput(int deviceId) {
        if (context == null || deviceId <= 0) return null;
        AudioManager am = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
        for (AudioDeviceInfo d : am.getDevices(AudioManager.GET_DEVICES_INPUTS))
            if (d.getId() == deviceId) return d;
        return null;
    }

    public static boolean isUnprocessedSupported(Context ctx) {
        AudioManager am = (AudioManager) ctx.getSystemService(Context.AUDIO_SERVICE);
        String v = am.getProperty(AudioManager.PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED);
        return v != null && Boolean.parseBoolean(v);
    }

    public boolean start(int source, int channelCount) { return start(source, channelCount, 0); }

    /** deviceId 0 = system default input; otherwise an id from listInputs(). */
    public boolean start(int source, int channelCount, int deviceId) {
        stop();
        AudioDeviceInfo device = findInput(deviceId);
        if (deviceId > 0 && device == null) { status = "input device " + deviceId + " is gone"; return false; }
        if (device != null && Build.VERSION.SDK_INT >= 31 &&
                (device.getType() == AudioDeviceInfo.TYPE_BLUETOOTH_SCO || device.getType() == AudioDeviceInfo.TYPE_BLE_HEADSET)) {
            // Bluetooth headset mics only record while routed as the communication device.
            commManager = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
            if (!commManager.setCommunicationDevice(device)) Log.w(TAG, "setCommunicationDevice refused");
        }
        channels = Math.max(1, Math.min(4, channelCount));
        int mask = channels == 2 ? AudioFormat.CHANNEL_IN_STEREO : AudioFormat.CHANNEL_IN_MONO;
        int min = AudioRecord.getMinBufferSize(RATE, channels > 2 ? AudioFormat.CHANNEL_IN_STEREO : mask, AudioFormat.ENCODING_PCM_FLOAT);
        if (min <= 0) { status = "getMinBufferSize failed: " + min; return false; }
        try {
            if (channels > 2) {
                // 3–4 channels: ask by index (mic 0, 1, 2…), not by position — the only way to request more than stereo.
                AudioFormat f = new AudioFormat.Builder().setEncoding(AudioFormat.ENCODING_PCM_FLOAT).setSampleRate(RATE)
                        .setChannelIndexMask((1 << channels) - 1).build();
                record = new AudioRecord.Builder().setAudioSource(source).setAudioFormat(f)
                        .setBufferSizeInBytes(Math.max(min * 4, RATE * 4 * channels / 5)).build();
            } else
            record = new AudioRecord(source, RATE, mask, AudioFormat.ENCODING_PCM_FLOAT, Math.max(min * 4, RATE * 4 * channels / 5));
        } catch (Exception e) {
            status = "AudioRecord failed: " + e.getMessage();
            return false;
        }
        if (record.getState() != AudioRecord.STATE_INITIALIZED) {
            status = "AudioRecord not initialized (source " + source + ", " + channels + " ch)";
            record.release();
            record = null;
            return false;
        }
        if (device != null && !record.setPreferredDevice(device)) Log.w(TAG, "setPreferredDevice refused for " + deviceId);
        int session = record.getAudioSessionId();
        String fx = disableEffects(session);
        record.startRecording();
        running = true;
        thread = new Thread(this::loop, "Week1RawMic");
        thread.start();
        status = "source " + source + ", " + record.getChannelCount() + " ch, " + record.getSampleRate() + " Hz, effects: " + fx
                + (device != null ? ", device " + device.getProductName() : "");
        Log.i(TAG, "started " + status);
        return true;
    }

    private static String disableEffects(int session) {
        StringBuilder sb = new StringBuilder();
        if (NoiseSuppressor.isAvailable()) {
            NoiseSuppressor ns = NoiseSuppressor.create(session);
            if (ns != null) { sb.append("NS was ").append(ns.getEnabled() ? "on" : "off"); ns.setEnabled(false); }
        } else sb.append("NS n/a");
        if (AutomaticGainControl.isAvailable()) {
            AutomaticGainControl agc = AutomaticGainControl.create(session);
            if (agc != null) { sb.append(", AGC was ").append(agc.getEnabled() ? "on" : "off"); agc.setEnabled(false); }
        } else sb.append(", AGC n/a");
        if (AcousticEchoCanceler.isAvailable()) {
            AcousticEchoCanceler aec = AcousticEchoCanceler.create(session);
            if (aec != null) { sb.append(", AEC was ").append(aec.getEnabled() ? "on" : "off"); aec.setEnabled(false); }
        } else sb.append(", AEC n/a");
        return sb.toString();
    }

    private void loop() {
        float[] buf = new float[RATE / 50 * channels]; // 20 ms
        while (running) {
            int n = record.read(buf, 0, buf.length, AudioRecord.READ_BLOCKING);
            if (n <= 0) continue;
            synchronized (lock) {
                if (ringCount + n > ring.length) ringCount = 0; // Unity stalled > 2 s: drop old audio
                System.arraycopy(buf, 0, ring, ringCount, n);
                ringCount += n;
            }
        }
    }

    /** Interleaved samples captured since the last call. */
    public float[] poll() {
        synchronized (lock) {
            float[] out = new float[ringCount];
            System.arraycopy(ring, 0, out, 0, ringCount);
            ringCount = 0;
            return out;
        }
    }

    public int getChannels() { return channels; }
    public int getSampleRate() { return RATE; }
    public String getStatus() { return status; }

    /** The device actually recording (Android may ignore the preferred one), e.g. "15 Quest 3". */
    public String getRoutedDevice() {
        if (record == null) return "";
        AudioDeviceInfo d = record.getRoutedDevice();
        return d == null ? "" : d.getType() + " " + d.getProductName();
    }

    public void stop() {
        running = false;
        if (thread != null) { try { thread.join(500); } catch (InterruptedException ignored) { } thread = null; }
        if (record != null) {
            try { record.stop(); } catch (Exception ignored) { }
            record.release();
            record = null;
        }
        if (commManager != null && Build.VERSION.SDK_INT >= 31) { commManager.clearCommunicationDevice(); commManager = null; }
        synchronized (lock) { ringCount = 0; }
    }
}
