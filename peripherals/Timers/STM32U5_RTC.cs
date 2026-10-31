//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 RTC, RM0456 Rev 7 section 63.
//
// Adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/Timers/STM32F4_RTC.cs @ 066a7f13 (Renode 1.17.0).
// The calendar, alarm, wake-up timer and write protection logic is unchanged. Changes:
// - U5 register map: SSR, CR, CALR and the alarm registers at their U5 offsets; no backup
//   registers (they are in TAMP).
// - Interrupt flags in RTC_SR, cleared through RTC_SCR, masked flags in RTC_MISR (non-secure
//   features) and RTC_SMISR (secure features).
// - Alarms and wake-up timer on two interrupt lines: IRQ (RTC_IRQn) for the non-secure
//   features, SecureIRQ (RTC_S_IRQn) for the secure features.
// - TrustZone and privilege protection (sections 63.3.4 and 63.3.5): RTC_SECCFGR and
//   RTC_PRIVCFGR deny the protected registers and bits to non-secure and unprivileged
//   accesses (read as 0, writes ignored). A denied non-secure access to a register protected
//   as a whole, except RTC_WPR, is reported to the TZIC. With trustZoneEnabled = false (TZEN =
//   0) every access is non-secure and RTC_SECCFGR stays 0.
// - New U5 fields are tags (binary mode).
// - The RTC_WPR key protects only the initialization and calibration registers; alarms,
//   wake-up timer and the other RTC_CR bits are written without it.
// - The calendar counts from reset, not only after the first exit from initialization mode.
// - The wake-up timer restarts only when WUTE changes, not on every RTC_CR write.
// - A system reset leaves the RTC running and configured (backup domain), except the
//   initialization mode and the write protection.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class STM32U5_RTC : IDoubleWordPeripheral, IKnownSize, ISTM32U5_TrustZoneAware
    {
        public STM32U5_RTC(IMachine machine, ulong wakeupTimerFrequency = DefaultWakeupTimerFrequency,
            bool trustZoneEnabled = false, STM32U5_GTZC_TZIC illegalAccessController = null)
        {
            sysbus = machine.GetSystemBus(this);
            this.trustZoneEnabled = trustZoneEnabled;
            this.illegalAccessController = illegalAccessController;
            mainTimer = new TimerConfig(this);
            alarmA = new AlarmConfig(this, mainTimer);
            alarmB = new AlarmConfig(this, mainTimer);

            IRQ = new GPIO();
            SecureIRQ = new GPIO();

            // The ticker reaches its limit at (wakeupTimerFrequency / (PREDIV_A + 1) / (PREDIV_S + 1)) Hz
            // The prediv values are usually chosen so that its frequency is 1 Hz but this is not required
            ticker = new LimitTimer(machine.ClockSource, wakeupTimerFrequency, this, nameof(ticker), DefaultSynchronuousPrescaler + 1, direction: Direction.Descending, eventEnabled: true, divider: DefaultAsynchronuousPrescaler + 1, autoUpdate: true);
            ticker.LimitReached += UpdateState;

            // The fastTicker reaches its limit once for every increment of the ticker. It is used to
            // implement subsecond alarm interrupts.
            fastTicker = new LimitTimer(machine.ClockSource, wakeupTimerFrequency, this, nameof(fastTicker), 1, direction: Direction.Ascending, eventEnabled: true, divider: DefaultAsynchronuousPrescaler + 1);
            fastTicker.LimitReached += UpdateAlarms;

            wakeupTimer = new LimitTimer(machine.ClockSource, wakeupTimerFrequency, this, nameof(wakeupTimer), direction: Direction.Ascending, autoUpdate: true);
            wakeupTimer.LimitReached += delegate
            {
                wakeupTimerFlag.Value = true; // reset by software
                UpdateInterrupts();
            };
            ResetInnerTimers();

            IFlagRegisterField syncFlag = null;

            var registerMap = new Dictionary<long, DoubleWordRegister>
            {
                {(long)Registers.TimeRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 4, out secondUnits, name: "SU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Second, Rank.Units))
                    .WithValueField(4, 3, out secondTens, name: "ST",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Second, Rank.Tens))
                    .WithReservedBits(7, 1)
                    .WithValueField(8, 4, out minuteUnits, name: "MU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Minute, Rank.Units))
                    .WithValueField(12, 3, out minuteTens, name: "MT",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Minute, Rank.Tens))
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 4, out hourUnits, name: "HU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Hour, Rank.Units))
                    .WithValueField(20, 2, out hourTens, name: "HT",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Hour, Rank.Tens))
                    .WithFlag(22, name: "PM",
                        writeCallback: (_, value) =>
                        {
                            if(CheckIfInInitMode(Registers.TimeRegister) && CheckIfUnlocked(Registers.TimeRegister))
                            {
                                mainTimer.PM = value;
                            }
                        },
                        valueProviderCallback: _ => mainTimer.PM)
                    .WithReservedBits(23, 9)
                    .WithWriteCallback((_, __) => UpdateTime())
                },
                {(long)Registers.DateRegister, new DoubleWordRegister(this, 0x2101)
                    .WithValueField(0, 4, out dayUnits, name: "DU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Day, Rank.Units))
                    .WithValueField(4, 2, out dayTens, name: "DT",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Day, Rank.Tens))
                    .WithReservedBits(6, 2)
                    .WithValueField(8, 4, out monthUnits, name: "MU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Month, Rank.Units))
                    .WithValueField(12, 1, out monthTens, name: "MT",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Month, Rank.Tens))
                    .WithValueField(13, 3, name: "WDU",
                        writeCallback: (_, value) =>
                        {
                            if(CheckIfInInitMode(Registers.DateRegister) && CheckIfUnlocked(Registers.DateRegister))
                            {
                                if(value == 0)
                                {
                                    this.Log(LogLevel.Warning, "Writting value 0 to WeekDay register is forbidden");
                                    return;
                                }
                                mainTimer.WeekDay = (DayOfTheWeek)value;
                            }
                        },
                        valueProviderCallback: _ => (uint)mainTimer.WeekDay)
                    .WithValueField(16, 4, out yearUnits, name: "YU",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Year, Rank.Units))
                    .WithValueField(20, 4, out yearTens, name: "YT",
                        valueProviderCallback: _ => mainTimer.Read(DateTimeSelect.Year, Rank.Tens))
                    .WithReservedBits(24, 8)
                    .WithWriteCallback((_, __) => UpdateDate())
                },
                {(long)Registers.ControlRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 3, out wakeupClockSelection, name: "WUCKSEL",
                        writeCallback: (_, value) =>
                        {
                            if((value & 0b100) == 0)
                            {
                                // 0xx: RTC / 2^(4 - xx) clock is selected
                                // 000: RTC / 2^4 = RTC / 16
                                // 011: RTC / 2^1 = RTC / 2
                                wakeupTimer.Divider = (ulong)Math.Pow(2, 4 - value);
                            }
                            else
                            {
                                // 1xx: ck_spre (usually 1 Hz) clock is selected
                                // ck_spre = RTC / {(PREDIV_S + 1) * (PREDIV_A + 1)}, see RM p.548
                                wakeupTimer.Divider = (predivS.Value + 1) * (predivA.Value + 1);
                            }
                        })
                    .WithTag("TSEDGE", 3, 1)
                    .WithTag("REFCKON", 4, 1)
                    .WithFlag(5, name: "BYPSHAD",
                        valueProviderCallback: _ => true, // Always report that shadow registers are bypassed
                        writeCallback: (_, value) =>
                        {
                            if(!value)
                            {
                                this.Log(LogLevel.Warning, "Shadow registers are not supported");
                            }
                        })
                    .WithFlag(6, name: "FMT",
                        writeCallback: (_, value) =>
                        {
                            if(CheckIfUnlocked(Registers.ControlRegister))
                            {
                                AMPMFormat = value;

                                mainTimer.ConfigureAMPM();
                                alarmA.ConfigureAMPM();
                                alarmB.ConfigureAMPM();
                            }
                        },
                        valueProviderCallback: _ => AMPMFormat)
                    .WithTag("SSRUIE", 7, 1)
                    .WithFlag(8, name: "ALRAE",
                        writeCallback: (_, value) =>
                        {
                            alarmA.Enable = value;
                        },
                        valueProviderCallback: _ => alarmA.Enable)
                    .WithFlag(9, name: "ALRBE",
                        writeCallback: (_, value) =>
                        {
                            alarmB.Enable = value;
                        },
                        valueProviderCallback: _ => alarmB.Enable)
                    .WithFlag(10, name: "WUTE",
                        writeCallback: (_, value) =>
                        {
                            if(value == wakeupTimer.Enabled)
                            {
                                return;
                            }
                            wakeupTimer.Enabled = value;
                            wakeupTimer.Value = 0;
                        },
                        valueProviderCallback: _ => wakeupTimer.Enabled)
                    .WithTag("TSE", 11, 1) // Timestamp not supported
                    .WithFlag(12, name: "ALRAIE",
                        writeCallback: (_, value) =>
                        {
                            alarmA.InterruptEnable = value;
                        },
                        valueProviderCallback: _ => alarmA.InterruptEnable)
                    .WithFlag(13, name: "ALRBIE",
                        writeCallback: (_, value) =>
                        {
                            alarmB.InterruptEnable = value;
                        },
                        valueProviderCallback: _ => alarmB.InterruptEnable)
                    .WithFlag(14, name: "WUTIE",
                        writeCallback: (_, value) =>
                        {
                            wakeupTimer.EventEnabled = value;
                        },
                        valueProviderCallback: _ => wakeupTimer.EventEnabled)
                    .WithTag("TSIE", 15, 1) // Timestamp not supported
                    .WithTag("ADD1H", 16, 1)
                    .WithTag("SUB1H", 17, 1)
                    .WithTag("BKP", 18, 1)
                    .WithTag("COSEL", 19, 1)
                    .WithTag("POL", 20, 1)
                    .WithTag("OSEL", 21, 2)
                    .WithTag("COE", 23, 1)
                    .WithTag("ITSE", 24, 1)
                    .WithTag("TAMPTS", 25, 1)
                    .WithTag("TAMPOE", 26, 1)
                    .WithTag("ALRAFCLR", 27, 1)
                    .WithTag("ALRBFCLR", 28, 1)
                    .WithTag("TAMPALRM_PU", 29, 1)
                    .WithTag("TAMPALRM_TYPE", 30, 1)
                    .WithTag("OUT2EN", 31, 1)
                },
                {(long)Registers.ICSR, new DoubleWordRegister(this, 0x7)
                    .WithReservedBits(0, 2)
                    .WithFlag(2, FieldMode.Read, valueProviderCallback: _ => !wakeupTimer.Enabled, name: "WUTWF")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "SHPF") // Shift operations not supported
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => mainTimer.TimeState.Year != 2000, name: "INITS")
                    .WithFlag(5, out syncFlag, FieldMode.Read | FieldMode.WriteZeroToClear, name: "RSF",
                              readCallback: (_, curr) =>
                              {
                                  // this strange logic is required by the Zephyr driver;
                                  // it wants to read 0 before reading 1, otherwise it times-out
                                  if(!curr)
                                  {
                                      syncFlag.Value = true;
                                  }
                              })
                    .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => initMode, name: "INITF")
                    .WithFlag(7, FieldMode.Write, name: "INIT",
                        changeCallback: (_, value) =>
                        {
                            if(CheckIfUnlocked(Registers.ICSR))
                            {
                                ticker.Enabled = !value;
                                fastTicker.Enabled = !value;
                                initMode = value;
                            }
                        })
                    .WithTag("BIN", 8, 2) // Binary mode not supported
                    .WithTag("BCDU", 10, 3)
                    .WithReservedBits(13, 3)
                    .WithFlag(16, FieldMode.Read, valueProviderCallback: _ => false, name: "RECALPF") // Recalibration not supported
                    .WithIgnoredBits(17, 15) // We don't use reserved bits because the HAL sometimes writes 0s here and sometimes 1s
                },
                {(long)Registers.PrescalerRegister, new DoubleWordRegister(this, DefaultAsynchronuousPrescaler << 16 | DefaultSynchronuousPrescaler)
                    .WithValueField(0, 15, out predivS, writeCallback: (_, value) => ticker.Limit = value + 1, name: "PREDIV_S")
                    .WithReservedBits(15, 1)
                    .WithValueField(16, 7, out predivA, writeCallback: (_, value) =>
                    {
                        ticker.Divider = value + 1;
                        fastTicker.Divider = value + 1;
                    }, name: "PREDIV_A")
                    .WithReservedBits(23, 9)
                },
                {(long)Registers.WakeupTimerRegister, new DoubleWordRegister(this, 0xFFFF)
                    .WithValueField(0, 16, out wakeupAutoReload, name: "WUT",
                        writeCallback: (_, value) =>
                        {
                            // WUCKSEL value: '11x' = 2^16 is added to the WUT counter value (see reference manual p.565)
                            if((wakeupClockSelection.Value & 0b110) == 0b110)
                            {
                                value += 0x10000;
                            }

                            // The wakeup timer flag needs to be set every (WUT + 1) cycles of the wakeup timer.
                            wakeupTimer.Limit = value + 1;
                        },
                        valueProviderCallback: _ => WakeupTimerRegisterErrata ? wakeupAutoReload.Value : (uint)wakeupTimer.Limit)
                    .WithTag("WUTOCLR", 16, 16)
                },
                {(long)Registers.AlarmARegister, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "SU",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Second, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Second, Rank.Units))
                    .WithValueField(4, 3, name: "ST",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Second, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Second, Rank.Tens))
                    .WithFlag(7, name: "MSK1",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.SecondsMask = value),
                        valueProviderCallback: _ => alarmA.SecondsMask)
                    .WithValueField(8, 4, name: "MU",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Minute, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Minute, Rank.Units))
                    .WithValueField(12, 3, name: "MT",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Minute, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Minute, Rank.Tens))
                    .WithFlag(15, name: "MSK2",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.MinutesMask = value),
                        valueProviderCallback: _ => alarmA.MinutesMask)
                    .WithValueField(16, 4, name: "HU",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Hour, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Hour, Rank.Units))
                    .WithValueField(20, 2, name: "HT",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Hour, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Hour, Rank.Tens))
                    .WithFlag(22, name: "PM",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.PM = value),
                        valueProviderCallback: _ => alarmA.PM)
                    .WithFlag(23, name: "MSK3",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.HoursMask = value),
                        valueProviderCallback: _ => alarmA.HoursMask)
                    .WithValueField(24, 4, name: "DU",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Day, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Day, Rank.Units))
                    .WithValueField(28, 2, name: "DT",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Update(DateTimeSelect.Day, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmA.Read(DateTimeSelect.Day, Rank.Tens))
                    .WithTag("WDSEL", 30, 1) // Weekday instead of date units usupported
                    .WithFlag(31, name: "MSK4",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.DaysMask = value),
                        valueProviderCallback: _ => alarmA.DaysMask)
                },
                {(long)Registers.AlarmBRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 4, name: "SU",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Second, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Second, Rank.Units))
                    .WithValueField(4, 3, name: "ST",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Second, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Second, Rank.Tens))
                    .WithFlag(7, name: "MSK1",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.SecondsMask = value),
                        valueProviderCallback: _ => alarmB.SecondsMask)
                    .WithValueField(8, 4, name: "MU",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Minute, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Minute, Rank.Units))
                    .WithValueField(12, 3, name: "MT",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Minute, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Minute, Rank.Tens))
                    .WithFlag(15, name: "MSK2",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.MinutesMask = value),
                        valueProviderCallback: _ => alarmB.MinutesMask)
                    .WithValueField(16, 4, name: "HU",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Hour, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Hour, Rank.Units))
                    .WithValueField(20, 2, name: "HT",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Hour, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Hour, Rank.Tens))
                    .WithFlag(22, name: "PM",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.PM = value),
                        valueProviderCallback: _ => alarmB.PM)
                    .WithFlag(23, name: "MSK3",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.HoursMask = value),
                        valueProviderCallback: _ => alarmB.HoursMask)
                    .WithValueField(24, 4, name: "DU",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Day, Rank.Units, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Day, Rank.Units))
                    .WithValueField(28, 2, name: "DT",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Update(DateTimeSelect.Day, Rank.Tens, (uint)value)),
                        valueProviderCallback: _ => alarmB.Read(DateTimeSelect.Day, Rank.Tens))
                    .WithTag("WDSEL", 30, 1) // Weekday instead of date units usupported
                    .WithFlag(31, name: "MSK4",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.DaysMask = value),
                        valueProviderCallback: _ => alarmB.DaysMask)
                },
                {(long)Registers.WriteProtectionRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 8, name: "KEY",
                        writeCallback: (_, value) =>
                        {
                            if(value == UnlockKey1 && !firstStageUnlocked)
                            {
                                firstStageUnlocked = true;
                            }
                            else if(value == UnlockKey2 && firstStageUnlocked)
                            {
                                registersUnlocked = true;
                                firstStageUnlocked = false;
                            }
                            else
                            {
                                firstStageUnlocked = false;
                                registersUnlocked = false;
                            }
                        },
                        valueProviderCallback: _ => 0)
                    .WithReservedBits(8, 24)
                },
                {(long)Registers.SubSecondRegister, new DoubleWordRegister(this)
                    // Descending timers have values in (0; limit], the subsecond field has to be in [0; limit)
                    .WithValueField(0, 16, FieldMode.Read, name: "SS", valueProviderCallback: _ => (uint)ticker.Value - 1)
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.ShiftControlRegister, new DoubleWordRegister(this)
                    .WithTag("SUBFS", 0, 15)
                    .WithReservedBits(15, 16)
                    .WithTag("ADD1S", 31, 1)
                },
                {(long)Registers.TimestampTimeRegister, new DoubleWordRegister(this)
                    .WithTag("Second", 0, 7)
                    .WithReservedBits(7, 1)
                    .WithTag("Minute", 8, 7)
                    .WithReservedBits(15, 1)
                    .WithTag("Hour", 16, 6)
                    .WithTag("PM", 22, 1)
                    .WithReservedBits(23, 9)
                },
                {(long)Registers.TimestampDateRegister, new DoubleWordRegister(this)
                    .WithTag("Day", 0, 6)
                    .WithReservedBits(6, 2)
                    .WithTag("Month", 8, 5)
                    .WithTag("WDU", 13, 3)
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.TimestampSubSecondRegister, new DoubleWordRegister(this)
                    .WithTag("SS", 0, 16)
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.ClockCalibrationRegister, new DoubleWordRegister(this)
                    .WithTag("CALM", 0, 9)
                    .WithReservedBits(9, 3)
                    .WithTag("LPCAL", 12, 1)
                    .WithTag("CALW16", 13, 1)
                    .WithTag("CALW8", 14, 1)
                    .WithTag("CALP", 15, 1)
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.AlarmASubSecondRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "SS",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.Subsecond = (int)value),
                        valueProviderCallback: _ => (uint)alarmA.Subsecond)
                    .WithReservedBits(15, 9)
                    .WithValueField(24, 4, name: "MASKSS",
                        writeCallback: (_, value) => UpdateAlarmA(alarm => alarm.SubsecondsMask = (uint)value),
                        valueProviderCallback: _ => alarmA.SubsecondsMask)
                    .WithReservedBits(28, 3)
                    .WithTaggedFlag("SSCLR", 31)
                },
                {(long)Registers.AlarmBSubSecondRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 15, name: "SS",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.Subsecond = (int)value),
                        valueProviderCallback: _ => (uint)alarmB.Subsecond)
                    .WithReservedBits(15, 9)
                    .WithValueField(24, 4, name: "MASKSS",
                        writeCallback: (_, value) => UpdateAlarmB(alarm => alarm.SubsecondsMask = (uint)value),
                        valueProviderCallback: _ => alarmB.SubsecondsMask)
                    .WithReservedBits(28, 3)
                    .WithTaggedFlag("SSCLR", 31)
                },
                {(long)Registers.PrivilegeModeControlRegister, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ALRAPRIV")
                    .WithFlag(1, name: "ALRBPRIV")
                    .WithFlag(2, name: "WUTPRIV")
                    .WithFlag(3, name: "TSPRIV")
                    .WithReservedBits(4, 9)
                    .WithFlag(13, name: "CALPRIV")
                    .WithFlag(14, name: "INITPRIV")
                    .WithFlag(15, name: "PRIV")
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.SecureConfigurationRegister, new DoubleWordRegister(this)
                    .WithFlag(0, name: "ALRASEC")
                    .WithFlag(1, name: "ALRBSEC")
                    .WithFlag(2, name: "WUTSEC")
                    .WithFlag(3, name: "TSSEC")
                    .WithReservedBits(4, 9)
                    .WithFlag(13, name: "CALSEC")
                    .WithFlag(14, name: "INITSEC")
                    .WithFlag(15, name: "SEC")
                    .WithReservedBits(16, 16)
                    .WithWriteCallback((_, __) => UpdateInterrupts())
                },
                {(long)Registers.StatusRegister, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => alarmA.Flag, name: "ALRAF")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => alarmB.Flag, name: "ALRBF")
                    .WithFlag(2, out wakeupTimerFlag, FieldMode.Read, name: "WUTF")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "TSF") // Timestamp not supported
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => false, name: "TSOVF")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: _ => false, name: "ITSF")
                    .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => false, name: "SSRUF")
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.MaskedInterruptStatusRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 7, FieldMode.Read, name: "MISR",
                        valueProviderCallback: _ => MaskedInterruptFlags() & ~SecureFeatureFlags())
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.SecureMaskedInterruptStatusRegister, new DoubleWordRegister(this)
                    .WithValueField(0, 7, FieldMode.Read, name: "SMISR",
                        valueProviderCallback: _ => MaskedInterruptFlags() & SecureFeatureFlags())
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.StatusClearRegister, new DoubleWordRegister(this)
                    .WithFlag(0, FieldMode.WriteOneToClear, name: "CALRAF",
                        writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                alarmA.Flag = false;
                            }
                        })
                    .WithFlag(1, FieldMode.WriteOneToClear, name: "CALRBF",
                        writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                alarmB.Flag = false;
                            }
                        })
                    .WithFlag(2, FieldMode.WriteOneToClear, name: "CWUTF",
                        writeCallback: (_, value) =>
                        {
                            if(value)
                            {
                                wakeupTimerFlag.Value = false;
                                UpdateInterrupts();
                            }
                        })
                    // We make the following bits flags instead of tags to reduce warnings in the log
                    // because they are interrupt flag clear bits
                    .WithFlag(3, FieldMode.WriteOneToClear, name: "CTSF")
                    .WithFlag(4, FieldMode.WriteOneToClear, name: "CTSOVF")
                    .WithFlag(5, FieldMode.WriteOneToClear, name: "CITSF")
                    .WithFlag(6, FieldMode.WriteOneToClear, name: "CSSRUF")
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.AlarmABinaryModeRegister, new DoubleWordRegister(this)
                    .WithTag("SS", 0, 32)
                },
                {(long)Registers.AlarmBBinaryModeRegister, new DoubleWordRegister(this)
                    .WithTag("SS", 0, 32)
                },
            };
            registers = new DoubleWordRegisterCollection(this, registerMap);
        }

        public uint ReadDoubleWord(long offset)
        {
            var denied = DeniedBits(offset, false);
            return denied == AllBits ? 0 : registers.Read(offset) & ~denied;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var denied = DeniedBits(offset, true);
            if(denied == AllBits)
            {
                return;
            }
            if(denied != 0)
            {
                // RTC_SCR clears the flags of the written ones; the other registers keep the
                // protected bits. INIT is write-only in the register map: it is kept from the
                // initialization mode.
                var kept = offset == (long)Registers.StatusClearRegister ? 0 : registers.Read(offset) & denied;
                if(offset == (long)Registers.ICSR && initMode)
                {
                    kept |= denied & InitializationModeBit;
                }
                value = (value & ~denied) | kept;
            }
            registers.Write(offset, value);
        }

        public void Reset()
        {
            // System reset: the RTC is in the backup domain
            firstStageUnlocked = false;
            registersUnlocked = false;
            initMode = false;
            ticker.Enabled = true;
            fastTicker.Enabled = true;
            UpdateInterrupts();
        }

        public bool WakeupTimerRegisterErrata { get; set; }

        // RM0456 section 63.3.4: the RTC clock and reset bits of the RCC are secure as soon as one
        // RTC function is secure.
        public bool HasSecureFunction => trustZoneEnabled && registers.Read((long)Registers.SecureConfigurationRegister) != 0;

        public long Size => 0x400;

        // RTC_IRQn: alarms and wake-up timer of the non-secure features.
        public GPIO IRQ { get; }

        // RTC_S_IRQn: alarms and wake-up timer of the secure features (TZEN = 1).
        public GPIO SecureIRQ { get; }

        private void ResetInnerTimers()
        {
            mainTimer.Reset();
            ticker.Reset();
            fastTicker.Reset();
            alarmA.Reset();
            alarmB.Reset();
            wakeupTimer.Reset();
            // The calendar counts until INIT is set
            ticker.Enabled = true;
            fastTicker.Enabled = true;
        }

        private void ResetInnerStatus()
        {
            firstStageUnlocked = false;
            registersUnlocked = false;
            initMode = false;
            AMPMFormat = false;
        }

        private void UpdateState()
        {
            var previousDayOfWeek = mainTimer.TimeState.DayOfWeek;

            mainTimer.TimeState = mainTimer.TimeState.AddSeconds(1);

            if(previousDayOfWeek != mainTimer.TimeState.DayOfWeek)
            {
                // we allow for the WeekDay to be de-synchornized from
                // the actual day of week calculated from the TimeState
                // (as this is how the HW works)
                mainTimer.WeekDay = (DayOfTheWeek)(((int)mainTimer.WeekDay) % 7) + 1;
            }
        }

        private void UpdateAlarms()
        {
            alarmA.UpdateInterruptFlag();
            alarmB.UpdateInterruptFlag();
        }

        private void UpdateInterrupts()
        {
            var state = 0u;

            state |= alarmA.Flag && alarmA.InterruptEnable ? AlarmAFlag : 0;
            state |= alarmB.Flag && alarmB.InterruptEnable ? AlarmBFlag : 0;

            state |= wakeupTimerFlag.Value ? WakeupTimerFlag : 0;

            var secure = SecureFeatureFlags();
            IRQ.Set((state & ~secure) != 0);
            SecureIRQ.Set((state & secure) != 0);
        }

        // RTC_SR bits of the masked interrupts (RTC_MISR | RTC_SMISR).
        private uint MaskedInterruptFlags()
        {
            var flags = 0u;
            flags |= alarmA.Flag && alarmA.InterruptEnable ? AlarmAFlag : 0;
            flags |= alarmB.Flag && alarmB.InterruptEnable ? AlarmBFlag : 0;
            flags |= wakeupTimerFlag.Value && wakeupTimer.EventEnabled ? WakeupTimerFlag : 0;
            return flags;
        }

        // RTC_SR bits of the secure features.
        private uint SecureFeatureFlags()
        {
            var configuration = registers.Read((long)Registers.SecureConfigurationRegister);
            if((configuration & GlobalProtection) != 0)
            {
                return InterruptFlags;
            }
            return FeatureBits(Registers.StatusRegister, configuration, true);
        }

        // The bits of the register at offset denied to the current access.
        private uint DeniedBits(long offset, bool write)
        {
            STM32U5_TrustZoneContext.TryGetTransaction(sysbus, out var secure, out var privileged);
            var denied = 0u;
            if(!secure || !trustZoneEnabled)
            {
                denied |= ProtectedBits(offset, write, Registers.SecureConfigurationRegister,
                    Registers.PrivilegeModeControlRegister, out var whole);
                if(trustZoneEnabled && whole && offset != (long)Registers.WriteProtectionRegister)
                {
                    // Only the registers protected as a whole are reported, not the mixed ones.
                    this.Log(LogLevel.Debug, "Non-secure {0} of {1} denied", write ? "write" : "read", (Registers)offset);
                    illegalAccessController?.ReportIllegalAccess(IllegalAccessRegister, IllegalAccessBit);
                }
            }
            if(!privileged)
            {
                denied |= ProtectedBits(offset, write, Registers.PrivilegeModeControlRegister,
                    Registers.SecureConfigurationRegister, out _);
            }
            return denied;
        }

        // The bits of the register at offset that the configuration register protects:
        // RTC_SECCFGR against non-secure accesses, RTC_PRIVCFGR against unprivileged accesses.
        // Both have the same layout. whole: the register is protected as a whole.
        private uint ProtectedBits(long offset, bool write, Registers configurationRegister,
            Registers otherConfigurationRegister, out bool whole)
        {
            whole = false;
            if(offset == (long)configurationRegister)
            {
                // RTC_SECCFGR is written by secure accesses only, RTC_PRIVCFGR by privileged ones.
                whole = write;
                return write ? AllBits : 0;
            }
            var configuration = registers.Read((long)configurationRegister);
            if((configuration & GlobalProtection) != 0)
            {
                if(!write && (Array.IndexOf(AlwaysReadable, (Registers)offset) >= 0
                    || (offset == (long)Registers.MaskedInterruptStatusRegister && configurationRegister == Registers.SecureConfigurationRegister)))
                {
                    return 0;
                }
                whole = true;
                return AllBits;
            }
            if(offset == (long)otherConfigurationRegister)
            {
                // For example INITPRIV is written by secure accesses only when INITSEC is set.
                return write ? configuration & FeatureProtections : 0;
            }
            var bits = FeatureBits((Registers)offset, configuration, write);
            whole = bits == AllBits;
            return bits;
        }

        private static uint FeatureBits(Registers register, uint configuration, bool write)
        {
            var bits = 0u;
            foreach(var rule in FeatureRules)
            {
                if(rule.Register == register && (configuration & rule.Feature) != 0 && (write || rule.ReadProtected))
                {
                    bits |= rule.Bits;
                }
            }
            return bits;
        }

        private bool CheckIfInInitMode(Registers reg)
        {
            if(initMode)
            {
                return true;
            }

            this.Log(LogLevel.Warning, "Writing to {0} allowed only in init mode", reg);
            return false;
        }

        private bool CheckIfUnlocked(Registers reg)
        {
            if(registersUnlocked)
            {
                return true;
            }

            this.Log(LogLevel.Warning, "Writing to {0} is allowed only when the register is unlocked", reg);
            return false;
        }

        private bool CheckIfDisabled(AlarmConfig timer)
        {
            var enabled = timer.Enable;
            if(!enabled)
            {
                return true;
            }

            this.Log(LogLevel.Warning, "Configuring {0} is allowed only when it is disabled", timer);
            return false;
        }

        private void UpdateAlarm(AlarmConfig alarm, Registers register, Action<AlarmConfig> action)
        {
            if(!CheckIfDisabled(alarm))
            {
                return;
            }

            action(alarm);
        }

        private void UpdateAlarmA(Action<AlarmConfig> action)
        {
            UpdateAlarm(alarmA, Registers.AlarmARegister, action);
        }

        private void UpdateAlarmB(Action<AlarmConfig> action)
        {
            UpdateAlarm(alarmB, Registers.AlarmBRegister, action);
        }

        private void UpdateTime()
        {
            if(!CheckIfInInitMode(Registers.TimeRegister) || !CheckIfUnlocked(Registers.TimeRegister))
            {
                return;
            }

            var dateTime = mainTimer.TimeState;

            var second = dateTime.Second
                .WithUpdatedRank((int)secondUnits.Value, Rank.Units)
                .WithUpdatedRank((int)secondTens.Value, Rank.Tens);

            var minute = dateTime.Minute
                .WithUpdatedRank((int)minuteUnits.Value, Rank.Units)
                .WithUpdatedRank((int)minuteTens.Value, Rank.Tens);

            var hour = dateTime.Hour
                .WithUpdatedRank((int)hourUnits.Value, Rank.Units)
                .WithUpdatedRank((int)hourTens.Value, Rank.Tens);

            mainTimer.TimeState = dateTime.With(
                hour: hour,
                minute: minute,
                second: second
            );
        }

        private void UpdateDate()
        {
            if(!CheckIfInInitMode(Registers.DateRegister) || !CheckIfUnlocked(Registers.DateRegister))
            {
                return;
            }

            var dateTime = mainTimer.TimeState;

            var day = dateTime.Day
                .WithUpdatedRank((int)dayUnits.Value, Rank.Units)
                .WithUpdatedRank((int)dayTens.Value, Rank.Tens);

            var month = dateTime.Month
                .WithUpdatedRank((int)monthUnits.Value, Rank.Units)
                .WithUpdatedRank((int)monthTens.Value, Rank.Tens);

            var year = dateTime.Year
                .WithUpdatedRank((int)yearUnits.Value, Rank.Units)
                .WithUpdatedRank((int)yearTens.Value, Rank.Tens);

            mainTimer.TimeState = dateTime.With(
                year: year,
                month: month,
                day: day
            );
        }

        private bool firstStageUnlocked;
        private bool registersUnlocked;
        private bool initMode;
        private bool AMPMFormat;

        private readonly TimerConfig mainTimer;
        private readonly AlarmConfig alarmA;
        private readonly AlarmConfig alarmB;
        // timestamp timer is currenlty not implementedw

        private readonly IValueRegisterField secondUnits;
        private readonly IValueRegisterField secondTens;
        private readonly IValueRegisterField minuteUnits;
        private readonly IValueRegisterField minuteTens;
        private readonly IValueRegisterField hourUnits;
        private readonly IValueRegisterField hourTens;

        private readonly IValueRegisterField dayUnits;
        private readonly IValueRegisterField dayTens;
        private readonly IValueRegisterField monthUnits;
        private readonly IValueRegisterField monthTens;
        private readonly IValueRegisterField yearUnits;
        private readonly IValueRegisterField yearTens;

        private readonly DoubleWordRegisterCollection registers;
        private readonly IValueRegisterField wakeupClockSelection;
        private readonly IValueRegisterField predivS;
        private readonly IValueRegisterField predivA;
        private readonly IFlagRegisterField wakeupTimerFlag;
        private readonly IValueRegisterField wakeupAutoReload;
        private readonly LimitTimer ticker;
        private readonly LimitTimer fastTicker;
        private readonly LimitTimer wakeupTimer;

        private const uint UnlockKey1 = 0xCA;
        private const uint UnlockKey2 = 0x53;
        private const long DefaultWakeupTimerFrequency = 32768;
        private const int DefaultSynchronuousPrescaler = 0xFF;
        private const int DefaultAsynchronuousPrescaler = 0x7F;

        private readonly IBusController sysbus;
        private readonly bool trustZoneEnabled;
        private readonly STM32U5_GTZC_TZIC illegalAccessController;

        private const uint AllBits = 0xFFFFFFFF;
        // RTC_ICSR.INIT.
        private const uint InitializationModeBit = 1u << 7;
        // RTC_SECCFGR and RTC_PRIVCFGR.
        private const uint AlarmAProtection = 1u << 0;
        private const uint AlarmBProtection = 1u << 1;
        private const uint WakeupTimerProtection = 1u << 2;
        private const uint TimestampProtection = 1u << 3;
        private const uint CalibrationProtection = 1u << 13;
        private const uint InitializationProtection = 1u << 14;
        private const uint GlobalProtection = 1u << 15;
        private const uint FeatureProtections = 0x600F;
        // RTC_SR, RTC_MISR, RTC_SMISR and RTC_SCR.
        private const uint AlarmAFlag = 1u << 0;
        private const uint AlarmBFlag = 1u << 1;
        private const uint WakeupTimerFlag = 1u << 2;
        private const uint InterruptFlags = 0x7F;
        // GTZC2_TZIC_IER2.RTCIE (RM0456 section 5.8.11).
        private const int IllegalAccessRegister = 2;
        private const int IllegalAccessBit = 1;

        // Readable by any access when SEC (or PRIV) is set; RTC_MISR also by non-secure accesses.
        private static readonly Registers[] AlwaysReadable =
        {
            Registers.SecureConfigurationRegister, Registers.PrivilegeModeControlRegister,
            Registers.TimeRegister, Registers.DateRegister, Registers.SubSecondRegister,
            Registers.PrescalerRegister, Registers.ClockCalibrationRegister
        };

        // RM0456 sections 63.3.4 and 63.3.5: the registers and bits of each feature. The
        // initialization and calibration registers stay readable.
        private static readonly ProtectionRule[] FeatureRules =
        {
            new ProtectionRule(InitializationProtection, Registers.TimeRegister, AllBits, false),
            new ProtectionRule(InitializationProtection, Registers.DateRegister, AllBits, false),
            new ProtectionRule(InitializationProtection, Registers.PrescalerRegister, AllBits, false),
            new ProtectionRule(InitializationProtection, Registers.ICSR, 0x1F80, false), // INIT, BIN, BCDU
            new ProtectionRule(InitializationProtection, Registers.ControlRegister, 0x40, false), // FMT
            new ProtectionRule(CalibrationProtection, Registers.ShiftControlRegister, AllBits, false),
            new ProtectionRule(CalibrationProtection, Registers.ClockCalibrationRegister, AllBits, false),
            new ProtectionRule(CalibrationProtection, Registers.ControlRegister, 0x30010, false), // ADD1H, SUB1H, REFCKON
            new ProtectionRule(AlarmAProtection, Registers.AlarmARegister, AllBits, true),
            new ProtectionRule(AlarmAProtection, Registers.AlarmASubSecondRegister, AllBits, true),
            new ProtectionRule(AlarmAProtection, Registers.AlarmABinaryModeRegister, AllBits, true),
            new ProtectionRule(AlarmAProtection, Registers.ControlRegister, 0x08001180, true), // ALRAE, ALRAFCLR, ALRAIE, SSRUIE
            new ProtectionRule(AlarmAProtection, Registers.StatusRegister, 0x41, true), // ALRAF, SSRUF
            new ProtectionRule(AlarmAProtection, Registers.MaskedInterruptStatusRegister, 0x41, true),
            new ProtectionRule(AlarmAProtection, Registers.SecureMaskedInterruptStatusRegister, 0x41, true),
            new ProtectionRule(AlarmAProtection, Registers.StatusClearRegister, 0x41, true),
            new ProtectionRule(AlarmBProtection, Registers.AlarmBRegister, AllBits, true),
            new ProtectionRule(AlarmBProtection, Registers.AlarmBSubSecondRegister, AllBits, true),
            new ProtectionRule(AlarmBProtection, Registers.AlarmBBinaryModeRegister, AllBits, true),
            new ProtectionRule(AlarmBProtection, Registers.ControlRegister, 0x10002200, true), // ALRBE, ALRBFCLR, ALRBIE
            new ProtectionRule(AlarmBProtection, Registers.StatusRegister, 0x2, true),
            new ProtectionRule(AlarmBProtection, Registers.MaskedInterruptStatusRegister, 0x2, true),
            new ProtectionRule(AlarmBProtection, Registers.SecureMaskedInterruptStatusRegister, 0x2, true),
            new ProtectionRule(AlarmBProtection, Registers.StatusClearRegister, 0x2, true),
            new ProtectionRule(WakeupTimerProtection, Registers.WakeupTimerRegister, AllBits, true),
            new ProtectionRule(WakeupTimerProtection, Registers.ControlRegister, 0x4407, true), // WUTE, WUTIE, WUCKSEL
            new ProtectionRule(WakeupTimerProtection, Registers.StatusRegister, 0x4, true),
            new ProtectionRule(WakeupTimerProtection, Registers.MaskedInterruptStatusRegister, 0x4, true),
            new ProtectionRule(WakeupTimerProtection, Registers.SecureMaskedInterruptStatusRegister, 0x4, true),
            new ProtectionRule(WakeupTimerProtection, Registers.StatusClearRegister, 0x4, true),
            new ProtectionRule(TimestampProtection, Registers.TimestampTimeRegister, AllBits, true),
            new ProtectionRule(TimestampProtection, Registers.TimestampDateRegister, AllBits, true),
            new ProtectionRule(TimestampProtection, Registers.TimestampSubSecondRegister, AllBits, true),
            new ProtectionRule(TimestampProtection, Registers.ControlRegister, 0x03008808, true), // TAMPTS, ITSE, TSE, TSIE, TSEDGE
            new ProtectionRule(TimestampProtection, Registers.StatusRegister, 0x38, true), // TSF, TSOVF, ITSF
            new ProtectionRule(TimestampProtection, Registers.MaskedInterruptStatusRegister, 0x38, true),
            new ProtectionRule(TimestampProtection, Registers.SecureMaskedInterruptStatusRegister, 0x38, true),
            new ProtectionRule(TimestampProtection, Registers.StatusClearRegister, 0x38, true),
        };

        private class ProtectionRule
        {
            public ProtectionRule(uint feature, Registers register, uint bits, bool readProtected)
            {
                Feature = feature;
                Register = register;
                Bits = bits;
                ReadProtected = readProtected;
            }

            public uint Feature { get; }
            public Registers Register { get; }
            public uint Bits { get; }
            public bool ReadProtected { get; }
        }

        private class TimerConfig
        {
            public TimerConfig(STM32U5_RTC parent)
            {
                this.parent = parent;
            }

            public void Reset()
            {
                timeState = new DateTime(2020, 1, 1);

                WeekDay = DayOfTheWeek.Monday;
                pm = false;
            }

            public uint Read(DateTimeSelect select, Rank rank)
            {
                var currentValue = GetTimeSelect(select);
                return (uint)currentValue.ReadRank(rank);
            }

            public void ConfigureAMPM()
            {
                if(!parent.AMPMFormat)
                {
                    return;
                }

                if(pm)
                {
                    if(TimeState.Hour < 12)
                    {
                        timeState = timeState.AddHours(12);
                    }
                }
                else
                {
                    if(TimeState.Hour >= 12)
                    {
                        timeState = timeState.AddHours(-12);
                    }
                }
            }

            public DateTime TimeState
            {
                get => timeState;

                set
                {
                    timeState = value;
                    ConfigureAMPM();
                }
            }

            public bool PM
            {
                get => timeState.Hour > 11 && parent.AMPMFormat;

                set
                {
                    pm = value;
                    ConfigureAMPM();
                }
            }

            // DateTime calculates the week day automatically based on the set date,
            // but the device allows for setting an arbitrary value
            public DayOfTheWeek WeekDay { get; set; }

            private int GetTimeSelect(DateTimeSelect what)
            {
                switch(what)
                {
                case DateTimeSelect.Second:
                    return timeState.Second;
                case DateTimeSelect.Minute:
                    return timeState.Minute;
                case DateTimeSelect.Hour:
                    return PM
                        ? timeState.Hour - 12
                        : timeState.Hour;
                case DateTimeSelect.Day:
                    return timeState.Day;
                case DateTimeSelect.Month:
                    return timeState.Month;
                case DateTimeSelect.Year:
                    return timeState.Year;
                default:
                    throw new ArgumentException($"Unexpected date time select: {what}");
                }
            }

            private DateTime timeState;
            private bool pm;

            private readonly STM32U5_RTC parent;
        }

        private class AlarmConfig
        {
            public AlarmConfig(STM32U5_RTC parent, TimerConfig masterTimer)
            {
                this.parent = parent;
                this.masterTimer = masterTimer;

                Reset();
            }

            public void Reset()
            {
                day = 0;

                hour = 0;
                minute = 0;
                second = 0;
                subsecond = 0;

                pm = false;
                enable = false;
                flag = false;
                interruptEnable = false;
                secondsMask = false;
                minutesMask = false;
                hoursMask = false;
                daysMask = false;
                subsecondsMask = 0;
            }

            public uint Read(DateTimeSelect select, Rank rank)
            {
                var currentValue = GetTimeSelect(select);
                return (uint)currentValue.ReadRank(rank);
            }

            public void ConfigureAMPM()
            {
                if(!parent.AMPMFormat)
                {
                    return;
                }

                if(pm)
                {
                    if(Hour < 12)
                    {
                        Hour += 12;
                    }
                }
                else
                {
                    if(Hour >= 12)
                    {
                        Hour -= 12;
                    }
                }
            }

            public void UpdateInterruptFlag()
            {
                // the initial value of `state` will be false
                // for a disabled timer
                var state = Enable;

                // Subseconds mask equal to 0 means the alarm is activated when the second unit is incremented
                // (or at most once every 1 second)
                if(SubsecondsMask == 0)
                {
                    state &= (parent.ticker.Value == parent.ticker.Limit);
                }
                else
                {
                    var ssComparedBitMask = (uint)BitHelper.Bits(0, (int)SubsecondsMask);
                    var maskedAlarmSubsecond = (ulong)(Subsecond & ssComparedBitMask);
                    var maskedCurrentSubsecond = parent.ticker.Value & ssComparedBitMask;
                    state &= (maskedAlarmSubsecond == maskedCurrentSubsecond);
                }

                if(!SecondsMask)
                {
                    state &= (Second == masterTimer.TimeState.Second);
                }
                if(!MinutesMask)
                {
                    state &= (Minute == masterTimer.TimeState.Minute);
                }
                if(!HoursMask)
                {
                    state &= (Hour == masterTimer.TimeState.Hour);
                }
                if(!DaysMask)
                {
                    // day of week not supported ATM
                    state &= (Day == masterTimer.TimeState.Day);
                }

                flag = state;
                parent.UpdateInterrupts();
            }

            public void Update(DateTimeSelect what, Rank rank, uint value)
            {
                var currentValue = GetTimeSelect(what);
                var val = currentValue.WithUpdatedRank((int)value, rank);

                switch(what)
                {
                case DateTimeSelect.Second:
                    Second = val;
                    break;
                case DateTimeSelect.Minute:
                    Minute = val;
                    break;
                case DateTimeSelect.Hour:
                    Hour = val;
                    break;
                case DateTimeSelect.Day:
                    Day = val;
                    break;
                default:
                    throw new ArgumentException($"Unexpected date time select: {what}");
                }
            }

            public int Day
            {
                get => day;

                set
                {
                    day = value;
                    UpdateInterruptFlag();
                }
            }

            public int Hour
            {
                get => hour;

                set
                {
                    hour = value;
                    UpdateInterruptFlag();
                }
            }

            public int Minute
            {
                get => minute;

                set
                {
                    minute = value;
                    UpdateInterruptFlag();
                }
            }

            public int Second
            {
                get => second;

                set
                {
                    second = value;
                    UpdateInterruptFlag();
                }
            }

            public int Subsecond
            {
                get => subsecond;

                set
                {
                    subsecond = value;
                    UpdateInterruptFlag();
                }
            }

            public bool PM
            {
                get => Hour > 11 && parent.AMPMFormat;

                set
                {
                    pm = value;
                    ConfigureAMPM();
                }
            }

            public bool Flag
            {
                get => flag;

                set
                {
                    if(value)
                    {
                        throw new ArgumentException("This field can only be explicitly cleared");
                    }

                    flag = false;
                    parent.UpdateInterrupts();
                }
            }

            public bool Enable
            {
                get => enable;

                set
                {
                    enable = value;
                    UpdateInterruptFlag();
                }
            }

            public bool InterruptEnable
            {
                get => interruptEnable;

                set
                {
                    interruptEnable = value;
                    UpdateInterruptFlag();
                }
            }

            // This is the number of compared least significant bits. 0 - no subsecond bits
            // are compared, 1..14 - that many LSBs are compared, 15 - all bits are compared
            public uint SubsecondsMask
            {
                get => subsecondsMask;

                set
                {
                    subsecondsMask = value;
                    UpdateInterruptFlag();
                }
            }

            public bool SecondsMask
            {
                get => secondsMask;

                set
                {
                    secondsMask = value;
                    UpdateInterruptFlag();
                }
            }

            public bool MinutesMask
            {
                get => minutesMask;

                set
                {
                    minutesMask = value;
                    UpdateInterruptFlag();
                }
            }

            public bool HoursMask
            {
                get => hoursMask;

                set
                {
                    hoursMask = value;
                    UpdateInterruptFlag();
                }
            }

            public bool DaysMask
            {
                get => daysMask;

                set
                {
                    daysMask = value;
                    UpdateInterruptFlag();
                }
            }

            private int GetTimeSelect(DateTimeSelect what)
            {
                switch(what)
                {
                case DateTimeSelect.Second:
                    return Second;
                case DateTimeSelect.Minute:
                    return Minute;
                case DateTimeSelect.Hour:
                    return PM
                        ? Hour - 12
                        : Hour;
                case DateTimeSelect.Day:
                    return Day;
                default:
                    throw new ArgumentException($"Unexpected date time select: {what}");
                }
            }

            // private DateTime timeState;
            private bool pm;
            private bool flag;
            private bool enable;
            private bool interruptEnable;
            private uint subsecondsMask;
            private bool secondsMask;
            private bool minutesMask;
            private bool hoursMask;
            private bool daysMask;

            private int day;
            private int subsecond;
            private int second;
            private int minute;
            private int hour;

            private readonly STM32U5_RTC parent;
            private readonly TimerConfig masterTimer;
        }

        private enum DayOfTheWeek
        {
            // 0 value is forbidden
            Monday = 1,
            Tuesday = 2,
            Wednesday = 3,
            Thursday = 4,
            Friday = 5,
            Saturday = 6,
            Sunday = 7
        }

        private enum DateTimeSelect
        {
            Second,
            Minute,
            Hour,
            Day,
            Month,
            Year
        }

        private enum Registers
        {
            TimeRegister = 0x0,
            DateRegister = 0x4,
            SubSecondRegister = 0x8,
            ICSR = 0xc,
            PrescalerRegister = 0x10,
            WakeupTimerRegister = 0x14,
            ControlRegister = 0x18,
            PrivilegeModeControlRegister = 0x1c,
            SecureConfigurationRegister = 0x20,
            WriteProtectionRegister = 0x24,
            ClockCalibrationRegister = 0x28,
            ShiftControlRegister = 0x2c,
            TimestampTimeRegister = 0x30,
            TimestampDateRegister = 0x34,
            TimestampSubSecondRegister = 0x38,
            AlarmARegister = 0x40,
            AlarmASubSecondRegister = 0x44,
            AlarmBRegister = 0x48,
            AlarmBSubSecondRegister = 0x4c,
            StatusRegister = 0x50,
            MaskedInterruptStatusRegister = 0x54,
            SecureMaskedInterruptStatusRegister = 0x58,
            StatusClearRegister = 0x5c,
            AlarmABinaryModeRegister = 0x70,
            AlarmBBinaryModeRegister = 0x74
        }
    }
}