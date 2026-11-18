package com.metastarter.week1bench;

import android.content.Context;
import android.content.SharedPreferences;
import android.security.keystore.KeyGenParameterSpec;
import android.security.keystore.KeyProperties;
import android.util.Base64;
import android.util.Log;

import java.nio.charset.StandardCharsets;
import java.security.KeyStore;

import javax.crypto.Cipher;
import javax.crypto.KeyGenerator;
import javax.crypto.SecretKey;
import javax.crypto.spec.GCMParameterSpec;

/**
 * Secrets (the Anthropic API key for online summaries) kept encrypted on the headset.
 *
 * An AES-256 key is made inside the Android keystore. It never leaves the keystore, so it can't be read or
 * copied, even by the app. Each secret is encrypted with it (AES-GCM) and the ciphertext is kept in the app's
 * private preferences, which other apps, USB file transfer and backups (allowBackup is off) can't reach.
 * A copy of those preferences is useless on any other device.
 * Secret values are never logged.
 */
public class SecureStore {
    private static final String TAG = "CaptionsSecureStore";
    private static final String ALIAS = "captions_secrets";
    private static final String PREFS = "captions_secure";

    private static SecretKey key() throws Exception {
        KeyStore ks = KeyStore.getInstance("AndroidKeyStore");
        ks.load(null);
        if (ks.containsAlias(ALIAS)) return ((KeyStore.SecretKeyEntry) ks.getEntry(ALIAS, null)).getSecretKey();
        KeyGenerator gen = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore");
        gen.init(new KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT | KeyProperties.PURPOSE_DECRYPT)
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build());
        return gen.generateKey();
    }

    private static SharedPreferences prefs(Context c) {
        return c.getApplicationContext().getSharedPreferences(PREFS, Context.MODE_PRIVATE);
    }

    /** Encrypts and keeps a secret; false if it couldn't be stored. */
    public static boolean put(Context c, String name, String value) {
        try {
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.ENCRYPT_MODE, key());
            byte[] ct = cipher.doFinal(value.getBytes(StandardCharsets.UTF_8));
            String packed = Base64.encodeToString(cipher.getIV(), Base64.NO_WRAP) + ":" + Base64.encodeToString(ct, Base64.NO_WRAP);
            return prefs(c).edit().putString(name, packed).commit();
        } catch (Exception e) {
            Log.w(TAG, "Couldn't store " + name + ": " + e.getClass().getSimpleName());
            return false;
        }
    }

    /** The secret, or null if there is none (or it can't be decrypted). */
    public static String get(Context c, String name) {
        String packed = prefs(c).getString(name, null);
        if (packed == null) return null;
        try {
            String[] parts = packed.split(":", 2);
            Cipher cipher = Cipher.getInstance("AES/GCM/NoPadding");
            cipher.init(Cipher.DECRYPT_MODE, key(), new GCMParameterSpec(128, Base64.decode(parts[0], Base64.NO_WRAP)));
            return new String(cipher.doFinal(Base64.decode(parts[1], Base64.NO_WRAP)), StandardCharsets.UTF_8);
        } catch (Exception e) {
            Log.w(TAG, "Couldn't read " + name + ": " + e.getClass().getSimpleName());
            return null;
        }
    }

    public static void remove(Context c, String name) {
        prefs(c).edit().remove(name).commit();
    }
}
