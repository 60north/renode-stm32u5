//
// Copyright (c) 2026 Fedor Druzhinin
//
// This file is licensed under the MIT License.
//
// STM32U5 hash processor (HASH), RM0456 Rev 7 section 52: SHA-1, MD5, SHA-224 and SHA-256,
// hash and HMAC modes (HMAC with the key, message, key sequence of three DCAL phases).
//
// The words written to HASH_DIN are swapped by DATATYPE and taken most significant byte
// first; HASH_STR NBLW gives the valid bits of the last word (whole bytes only). The digest
// is computed at DCAL: DCIS is set at once, BUSY reads 0 and DINIS 1 (the input FIFO is never
// full). The digest registers HASH_HR0-7 (0x310, HR0-4 also at 0x0C) hold the digest most
// significant byte first, as STM32Cube HAL_HASH reads them. The context swap registers keep
// their values but a context restore does not restore a partial computation. DMA requests are
// not modelled.
//
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Miscellaneous
{
    public class STM32U5_HASH : BasicDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_HASH(IMachine machine) : base(machine)
        {
            IRQ = new GPIO();
            DefineRegisters();
            Reset();
        }

        public override void Reset()
        {
            base.Reset();
            message.Clear();
            key = null;
            hmacPhase = 0;
            digest = new byte[MaximumDigestLength];
            wordsPushed = 0;
            UpdateInterrupts();
        }

        public long Size => 0x400;

        public GPIO IRQ { get; }

        private void DefineRegisters()
        {
            Registers.Control.Define(this)
                .WithReservedBits(0, 2)
                .WithFlag(2, FieldMode.Write, name: "INIT", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        Initialize();
                    }
                })
                .WithTaggedFlag("DMAE", 3)
                .WithEnumField(4, 2, out dataType, name: "DATATYPE")
                .WithFlag(6, out hmacMode, name: "MODE")
                .WithReservedBits(7, 1)
                .WithValueField(8, 4, FieldMode.Read, name: "NBW", valueProviderCallback: _ => (ulong)Math.Min(wordsPushed, 15))
                .WithFlag(12, FieldMode.Read, name: "DINNE", valueProviderCallback: _ => wordsPushed != 0)
                .WithFlag(13, name: "MDMAT")
                .WithReservedBits(14, 2)
                .WithFlag(16, out longKey, name: "LKEY")
                .WithEnumField(17, 2, out algorithm, name: "ALGO")
                .WithReservedBits(19, 13);

            Registers.DataInput.Define(this)
                .WithValueField(0, 32, FieldMode.Write, name: "DATAIN", writeCallback: (_, value) => PushWord((uint)value));

            Registers.Start.Define(this)
                .WithValueField(0, 5, out validBitsInLastWord, name: "NBLW")
                .WithReservedBits(5, 3)
                .WithFlag(8, FieldMode.Write, name: "DCAL", writeCallback: (_, value) =>
                {
                    if(value)
                    {
                        Calculate();
                    }
                })
                .WithReservedBits(9, 23);

            for(var i = 0; i < 5; i++)
            {
                var index = i;
                RegistersCollection.DefineRegister((long)Registers.Digest0 + i * 4)
                    .WithValueField(0, 32, FieldMode.Read, name: $"HR{i}", valueProviderCallback: _ => DigestWord(index));
            }

            Registers.InterruptEnable.Define(this)
                .WithFlag(0, out dataInputInterruptEnable, name: "DINIE")
                .WithFlag(1, out digestCompleteInterruptEnable, name: "DCIE")
                .WithReservedBits(2, 30)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            Registers.Status.Define(this, 0x1)
                .WithFlag(0, out dataInputReady, FieldMode.Read | FieldMode.WriteZeroToClear, name: "DINIS")
                .WithFlag(1, out digestComplete, FieldMode.Read | FieldMode.WriteZeroToClear, name: "DCIS")
                .WithFlag(2, FieldMode.Read, name: "DMAS", valueProviderCallback: _ => false)
                .WithFlag(3, FieldMode.Read, name: "BUSY", valueProviderCallback: _ => false)
                .WithReservedBits(4, 5)
                .WithValueField(9, 5, FieldMode.Read, name: "NBWP", valueProviderCallback: _ => (ulong)(wordsPushed % BlockWords))
                .WithReservedBits(14, 1)
                .WithFlag(15, FieldMode.Read, name: "DINNE", valueProviderCallback: _ => wordsPushed != 0)
                .WithValueField(16, 5, FieldMode.Read, name: "NBWE", valueProviderCallback: _ => (ulong)(BlockWords - wordsPushed % BlockWords))
                .WithReservedBits(21, 11)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            for(var i = 0; i < NumberOfContextRegisters; i++)
            {
                RegistersCollection.DefineRegister((long)Registers.ContextSwap0 + i * 4)
                    .WithValueField(0, 32, name: $"CSR{i}");
            }

            for(var i = 0; i < 8; i++)
            {
                var index = i;
                RegistersCollection.DefineRegister((long)Registers.DigestExtended0 + i * 4)
                    .WithValueField(0, 32, FieldMode.Read, name: $"HR{i}", valueProviderCallback: _ => DigestWord(index));
            }
        }

        private void Initialize()
        {
            message.Clear();
            key = null;
            hmacPhase = 0;
            wordsPushed = 0;
            digestComplete.Value = false;
            dataInputReady.Value = true;
            UpdateInterrupts();
        }

        private void PushWord(uint value)
        {
            uint word;
            switch(dataType.Value)
            {
            case DataType.HalfWord:
                word = (value << 16) | (value >> 16);
                break;
            case DataType.Byte:
                word = (value >> 24) | ((value >> 8) & 0xFF00) | ((value << 8) & 0xFF0000) | (value << 24);
                break;
            case DataType.Bit:
                word = 0;
                for(var i = 0; i < 32; i++)
                {
                    if((value & (1u << i)) != 0)
                    {
                        word |= 1u << (31 - i);
                    }
                }
                break;
            default:
                word = value;
                break;
            }
            message.Add((byte)(word >> 24));
            message.Add((byte)(word >> 16));
            message.Add((byte)(word >> 8));
            message.Add((byte)word);
            wordsPushed++;
            dataInputReady.Value = true;
            UpdateInterrupts();
        }

        // The data of the current phase: the pushed words without the invalid bytes of the last one.
        private byte[] TakePhaseData()
        {
            var validBits = (int)validBitsInLastWord.Value;
            if(validBits % 8 != 0)
            {
                this.Log(LogLevel.Warning, "NBLW = {0}: only whole bytes are supported, rounding up", validBits);
            }
            var length = message.Count;
            if(validBits != 0 && length >= 4)
            {
                length -= 4 - (validBits + 7) / 8;
            }
            var data = message.Take(length).ToArray();
            message.Clear();
            wordsPushed = 0;
            return data;
        }

        private void Calculate()
        {
            var data = TakePhaseData();
            if(!hmacMode.Value)
            {
                SetDigest(Hash(data));
                return;
            }
            // HMAC (RM0456 52.4.7): key, DCAL; message, DCAL; key, DCAL gives the HMAC.
            switch(hmacPhase)
            {
            case 0:
                key = data;
                hmacPhase = 1;
                dataInputReady.Value = true;
                break;
            case 1:
                hmacMessage = data;
                hmacPhase = 2;
                dataInputReady.Value = true;
                break;
            default:
                SetDigest(Hmac(key, hmacMessage));
                hmacPhase = 0;
                break;
            }
            UpdateInterrupts();
        }

        private byte[] Hmac(byte[] hmacKey, byte[] data)
        {
            var paddedKey = new byte[BlockWords * 4];
            var keyBytes = hmacKey.Length > paddedKey.Length ? Hash(hmacKey) : hmacKey;
            if(hmacKey.Length > paddedKey.Length != longKey.Value)
            {
                this.Log(LogLevel.Warning, "LKEY = {0} for a {1}-byte key", longKey.Value, hmacKey.Length);
            }
            Array.Copy(keyBytes, paddedKey, keyBytes.Length);
            var inner = paddedKey.Select(b => (byte)(b ^ 0x36)).Concat(data).ToArray();
            var outer = paddedKey.Select(b => (byte)(b ^ 0x5C)).Concat(Hash(inner)).ToArray();
            return Hash(outer);
        }

        private byte[] Hash(byte[] data)
        {
            switch(algorithm.Value)
            {
            case Algorithm.SHA1:
                using(var sha1 = SHA1.Create())
                {
                    return sha1.ComputeHash(data);
                }
            case Algorithm.MD5:
                using(var md5 = MD5.Create())
                {
                    return md5.ComputeHash(data);
                }
            case Algorithm.SHA224:
                return Sha256Family(data, Sha224InitialHash, 28);
            default:
                return Sha256Family(data, Sha256InitialHash, 32);
            }
        }

        private void SetDigest(byte[] value)
        {
            digest = new byte[MaximumDigestLength];
            Array.Copy(value, digest, value.Length);
            digestComplete.Value = true;
            dataInputReady.Value = true;
            UpdateInterrupts();
        }

        private ulong DigestWord(int index)
        {
            return ((ulong)digest[index * 4] << 24) | ((ulong)digest[index * 4 + 1] << 16) | ((ulong)digest[index * 4 + 2] << 8) | digest[index * 4 + 3];
        }

        private void UpdateInterrupts()
        {
            IRQ.Set((dataInputReady.Value && dataInputInterruptEnable.Value) || (digestComplete.Value && digestCompleteInterruptEnable.Value));
        }

        // FIPS 180-4 SHA-256 and SHA-224 (SHA-224: other initial hash value, truncated digest).
        private static byte[] Sha256Family(byte[] data, uint[] initialHash, int digestLength)
        {
            var h = (uint[])initialHash.Clone();
            var bitLength = (ulong)data.Length * 8;
            var paddedLength = (data.Length + 9 + 63) / 64 * 64;
            var padded = new byte[paddedLength];
            Array.Copy(data, padded, data.Length);
            padded[data.Length] = 0x80;
            for(var i = 0; i < 8; i++)
            {
                padded[paddedLength - 1 - i] = (byte)(bitLength >> (8 * i));
            }
            var w = new uint[64];
            for(var block = 0; block < paddedLength; block += 64)
            {
                for(var t = 0; t < 16; t++)
                {
                    w[t] = (uint)(padded[block + 4 * t] << 24 | padded[block + 4 * t + 1] << 16 | padded[block + 4 * t + 2] << 8 | padded[block + 4 * t + 3]);
                }
                for(var t = 16; t < 64; t++)
                {
                    var s0 = RotateRight(w[t - 15], 7) ^ RotateRight(w[t - 15], 18) ^ (w[t - 15] >> 3);
                    var s1 = RotateRight(w[t - 2], 17) ^ RotateRight(w[t - 2], 19) ^ (w[t - 2] >> 10);
                    w[t] = w[t - 16] + s0 + w[t - 7] + s1;
                }
                uint a = h[0], b = h[1], c = h[2], d = h[3], e = h[4], f = h[5], g = h[6], hh = h[7];
                for(var t = 0; t < 64; t++)
                {
                    var t1 = hh + (RotateRight(e, 6) ^ RotateRight(e, 11) ^ RotateRight(e, 25)) + ((e & f) ^ (~e & g)) + Sha256RoundConstants[t] + w[t];
                    var t2 = (RotateRight(a, 2) ^ RotateRight(a, 13) ^ RotateRight(a, 22)) + ((a & b) ^ (a & c) ^ (b & c));
                    hh = g;
                    g = f;
                    f = e;
                    e = d + t1;
                    d = c;
                    c = b;
                    b = a;
                    a = t1 + t2;
                }
                h[0] += a;
                h[1] += b;
                h[2] += c;
                h[3] += d;
                h[4] += e;
                h[5] += f;
                h[6] += g;
                h[7] += hh;
            }
            var result = new byte[digestLength];
            for(var i = 0; i < digestLength; i++)
            {
                result[i] = (byte)(h[i / 4] >> (24 - 8 * (i % 4)));
            }
            return result;
        }

        private static uint RotateRight(uint value, int count)
        {
            return (value >> count) | (value << (32 - count));
        }

        private IEnumRegisterField<DataType> dataType;
        private IEnumRegisterField<Algorithm> algorithm;
        private IFlagRegisterField hmacMode;
        private IFlagRegisterField longKey;
        private IValueRegisterField validBitsInLastWord;
        private IFlagRegisterField dataInputInterruptEnable;
        private IFlagRegisterField digestCompleteInterruptEnable;
        private IFlagRegisterField dataInputReady;
        private IFlagRegisterField digestComplete;

        private byte[] digest;
        private byte[] key;
        private byte[] hmacMessage;
        private int hmacPhase;
        private int wordsPushed;

        private readonly List<byte> message = new List<byte>();

        private const int BlockWords = 16;
        private const int MaximumDigestLength = 32;
        private const int NumberOfContextRegisters = 54;

        private static readonly uint[] Sha256InitialHash =
        {
            0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19
        };

        private static readonly uint[] Sha224InitialHash =
        {
            0xc1059ed8, 0x367cd507, 0x3070dd17, 0xf70e5939, 0xffc00b31, 0x68581511, 0x64f98fa7, 0xbefa4fa4
        };

        private static readonly uint[] Sha256RoundConstants =
        {
            0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5,
            0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
            0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da,
            0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
            0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85,
            0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
            0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3,
            0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2
        };

        private enum DataType
        {
            Word = 0,
            HalfWord = 1,
            Byte = 2,
            Bit = 3,
        }

        private enum Algorithm
        {
            SHA1 = 0,
            MD5 = 1,
            SHA224 = 2,
            SHA256 = 3,
        }

        private enum Registers
        {
            Control = 0x00,
            DataInput = 0x04,
            Start = 0x08,
            Digest0 = 0x0C,
            InterruptEnable = 0x20,
            Status = 0x24,
            ContextSwap0 = 0xF8,
            DigestExtended0 = 0x310,
        }
    }
}
