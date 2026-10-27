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
// - Interrupt flags in RTC_SR, cleared through RTC_SCR, masked flags in RTC_MISR.
// - One interrupt line for alarms and wake-up timer (RTC global interrupt).
// - New U5 fields are tags (binary mode, privilege and security configuration).
// - The RTC_WPR key protects only the initialization and calibration registers; alarms,
//   wake-up timer and the other RTC_CR bits are written without it.
// - The calendar counts from reset, not only after the first exit from initialization mode.
// - A system reset leaves the RTC running and configured (backup domain), except the
//   initialization mode and the write protection.
//
using System;
using System.Collections.Generic;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.Timers
{
    public class STM32U5_RTC : IDoubleWordPeripheral, IKnownSize
    {
        public STM32U5_RTC(IMachine machine, ulong wakeupTimerFrequency = DefaultWakeupTimerFrequency)
        {
            mainTimer = new TimerConfig(this);
            alarmA = new AlarmConfig(this, mainTimer);
            alarmB = new AlarmConfig(this, mainTimer);

            IRQ = new GPIO();

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
                    .WithTaggedFlag("ALRAPRIV", 0)
                    .WithTaggedFlag("ALRBPRIV", 1)
                    .WithTaggedFlag("WUTPRIV", 2)
                    .WithTaggedFlag("TSPRIV", 3)
                    .WithReservedBits(4, 9)
                    .WithTaggedFlag("CALPRIV", 13)
                    .WithTaggedFlag("INITPRIV", 14)
                    .WithTaggedFlag("PRIV", 15)
                    .WithReservedBits(16, 16)
                },
                {(long)Registers.SecureConfigurationRegister, new DoubleWordRegister(this)
                    .WithTaggedFlag("ALRASEC", 0)
                    .WithTaggedFlag("ALRBSEC", 1)
                    .WithTaggedFlag("WUTSEC", 2)
                    .WithTaggedFlag("TSSEC", 3)
                    .WithReservedBits(4, 9)
                    .WithTaggedFlag("CALSEC", 13)
                    .WithTaggedFlag("INITSEC", 14)
                    .WithTaggedFlag("SEC", 15)
                    .WithReservedBits(16, 16)
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
                    .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => alarmA.Flag && alarmA.InterruptEnable, name: "ALRAMF")
                    .WithFlag(1, FieldMode.Read, valueProviderCallback: _ => alarmB.Flag && alarmB.InterruptEnable, name: "ALRBMF")
                    .WithFlag(2, FieldMode.Read, valueProviderCallback: _ => wakeupTimerFlag.Value && wakeupTimer.EventEnabled, name: "WUTMF")
                    .WithFlag(3, FieldMode.Read, valueProviderCallback: _ => false, name: "TSMF")
                    .WithFlag(4, FieldMode.Read, valueProviderCallback: _ => false, name: "TSOVMF")
                    .WithFlag(5, FieldMode.Read, valueProviderCallback: _ => false, name: "ITSMF")
                    .WithFlag(6, FieldMode.Read, valueProviderCallback: _ => false, name: "SSRUMF")
                    .WithReservedBits(7, 25)
                },
                {(long)Registers.SecureMaskedInterruptStatusRegister, new DoubleWordRegister(this)
                    .WithTag("SMISR", 0, 7)
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
            return registers.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
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

        public long Size => 0x400;

        public GPIO IRQ { get; }

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
            var state = false;

            state |= alarmA.Flag && alarmA.InterruptEnable;
            state |= alarmB.Flag && alarmB.InterruptEnable;

            state |= wakeupTimerFlag.Value;

            IRQ.Set(state);
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