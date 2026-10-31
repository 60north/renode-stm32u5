//
// Copyright (c) 2026 Fedor Druzhinin
// Copyright (c) 2010-2026 Antmicro
//
// This file is licensed under the MIT License.
//
// STM32U5 secure digital input/output MultiMediaCard interface (SDMMC v2), RM0456 Rev 7
// section 31, with an STM32U5_SDCard.
//
// Adapted from renode-infrastructure
//   src/Emulator/Peripherals/Peripherals/SD/STM32SDMMC.cs and STM32HSDMMC.cs @ 7ea0541f8d9b
// (identical in Renode 1.17.0), merged into one class. Changes: SDMMC_STA and SDMMC_MASK in the
// SDMMC v2 layout (upstream keeps the F7 one); the internal DMA (IDMA) in single buffer and
// linked list modes, each buffer transferred after its time on the SDMMC bus (kernel clock,
// CLKDIV, WIDBUS, DDR), with IDMABTC, IDMATE and DPSMACT; CMD6 switch function status data;
// TXUNDERR and RXOVERR flags; register block size 0x400. CRC, timeouts, busy signalling, boot,
// SDIO and voltage switching are not modelled, as upstream.
//
using System;
using System.Collections.Generic;
using System.Linq;

using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Peripherals.Timers;
using Antmicro.Renode.Time;
using Antmicro.Renode.Utilities;

using SDCardAppCommand = Antmicro.Renode.Peripherals.SD.STM32U5_SDCard.SdCardApplicationSpecificCommand;
using SDCardCommand = Antmicro.Renode.Peripherals.SD.STM32U5_SDCard.SdCardCommand;

namespace Antmicro.Renode.Peripherals.SD
{
    /*
    * upstream description of the base model:
    * minimal SDMMC model working with zephyr driver and STM32 HAL for FAT and EXT2:
    *
    * references:
    *   - https://www.st.com/resource/en/reference_manual/dm00124865.pdf
    *   Zephyr
    *   - https://github.com/zephyrproject-rtos/zephyr/blob/c008cbab1a05316139de191b0553ab6ccc0073ad/drivers/disk/sdmmc_stm32.c
    *   STM HAL
    *   - https://github.com/STMicroelectronics/stm32f7xx_hal_driver/blob/master/Src/stm32f7xx_hal_sd.c
    *   - https://github.com/STMicroelectronics/stm32f7xx_hal_driver/blob/master/Src/stm32f7xx_ll_sdmmc.c
    */
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class STM32U5_SDMMC : NullRegistrationPointPeripheralContainer<STM32U5_SDCard>, IDoubleWordPeripheral, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IKnownSize
    {
        public STM32U5_SDMMC(IMachine machine, long kernelClockFrequency = DefaultKernelClockFrequency) : base(machine)
        {
            IRQ = new GPIO();
            sysbus = machine.GetSystemBus(this);
            idmaTimer = new LimitTimer(machine.ClockSource, DefaultKernelClockFrequency, this, "idma", direction: Direction.Ascending, workMode: WorkMode.OneShot, eventEnabled: true);
            idmaTimer.LimitReached += FinishIdmaBuffer;
            KernelClockFrequency = kernelClockFrequency;
            RegistersCollection = new DoubleWordRegisterCollection(this);
            responseFields = new IValueRegisterField[4];
            ReadDataBuffer = new Queue<byte>();
            InitializeRegisters();
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public override void Reset()
        {
            RegistersCollection.Reset();
            GoIdle();
        }

        public long Size => 0x400;

        // SDMMC kernel clock (RCC_CCIPR2 SDMMCSEL: CLK48 or PLL1 P), sets the IDMA transfer time.
        public long KernelClockFrequency
        {
            get => kernelClockFrequency;
            set
            {
                if(value <= 0)
                {
                    throw new RecoverableException("Kernel clock frequency must be positive");
                }
                kernelClockFrequency = value;
                idmaTimer.Frequency = (ulong)value;
            }
        }

        public GPIO IRQ { get; private set; }

        public DoubleWordRegisterCollection RegistersCollection { get; }

        protected virtual void ReadCard(STM32U5_SDCard sdCard, uint size)
        {
            var data = ReadCardData(sdCard, size);
            ReadDataBuffer.EnqueueRange(data);
        }

        protected virtual void WriteCard(STM32U5_SDCard sdCard, uint data)
        {
            sdCard.WriteData(data.AsRawBytes());
        }

        private void InitializeRegisters()
        {
            Registers.Power.Define(this)
                .WithValueField(0, 2, name: "SDMMC state control bits (PWRCTRL)", valueProviderCallback: _ => 3)
                .WithTaggedFlag("Voltage switch sequence start (VSWITCH)", 2)
                .WithTaggedFlag("Voltage switch procedure enable (VSWITCHEN)", 3)
                .WithTaggedFlag("Data and command direction signals polarity selection (DIRPOL)", 4)
                .WithReservedBits(5, 27);

            Clock = Registers.Clock.Define(this)
                .WithValueField(0, ClkDivWidth, out clockDivider, name: "Clock divide factor (CLKDIV)");

            Registers.Arg.Define(this)
                .WithValueField(0, 32, out var commandArgument, name: "Command argument (CMDARG)");

            Cmd = Registers.Cmd.Define(this)
                .WithEnumField<DoubleWordRegister, SDCardCommand>(0, 6, out var commandIndex, name: "Command index (CMDINDEX)")
                .WithEnumField<DoubleWordRegister, ResponseWidth>(CommandFieldsOffset, 2, out var responseWidth, name: "Wait for response bits (WAITRESP)")
                .WithTaggedFlag("CPSM waits for interrupt request (WAITINT)", CommandFieldsOffset + 2)
                .WithTaggedFlag("CPSM waits for end of data transfer from DPSM (WAITPEND)", CommandFieldsOffset + 3)
                .WithFlag(CommandFieldsOffset + 4, out var commandStarted, FieldMode.Read | FieldMode.WriteToClear, name: "Command path state machine (CPSM) enable bit (CPSMEN)", writeCallback: (_, value) =>
                {
                    if(!value) return;
                    var sdCard = RegisteredPeripheral;
                    if(sdCard == null)
                    {
                        this.WarningLog("Tried to send a command with index {0}, but no SD card is currently attached", commandIndex);
                        cTimeout.Value = true;
                        UpdateInterrupts();
                        return;
                    }
                    cmdSent.Value = true;
                    cmdREnd.Value = true;
                    UpdateInterrupts();

                    var wasAppCommand = sdCard.TreatNextCommandAsAppCommand;
                    var commandResult = sdCard.HandleCommand((uint) commandIndex.Value, (uint) commandArgument.Value);

                    switch(responseWidth.Value)
                    {
                    case ResponseWidth.LongResponse:
                        responseFields[3].Value = commandResult.AsUInt32(0);
                        responseFields[3].Value &= 0xFFFFFFFE; /* lsb is always = 0 */
                        responseFields[2].Value = commandResult.AsUInt32(32);
                        responseFields[1].Value = commandResult.AsUInt32(64);
                        responseFields[0].Value = commandResult.AsUInt32(96);
                        break;
                    case ResponseWidth.ShortResponse:
                    case ResponseWidth.ShortResponseNoCrc:
                        responseFields[0].Value = commandResult.AsUInt32(0);
                        break;
                    case ResponseWidth.NoResponse:
                        break;
                    }
                    ProcessCommand(sdCard, commandIndex.Value, wasAppCommand);
                });

            Registers.RespCmd.Define(this)
                .WithValueField(0, 6, name: "Response command index (RESPCMD)", valueProviderCallback: _ => (ulong)commandIndex.Value)
                .WithReservedBits(6, 26);

            Registers.Resp1.Define(this)
                .WithValueField(0, 32, out responseFields[0], FieldMode.Read, name: "Card status (CARDSTATUS1)");
            Registers.Resp2.Define(this)
                .WithValueField(0, 32, out responseFields[1], FieldMode.Read, name: "Card status (CARDSTATUS2)");
            Registers.Resp3.Define(this)
                .WithValueField(0, 32, out responseFields[2], FieldMode.Read, name: "Card status (CARDSTATUS3)");
            Registers.Resp4.Define(this)
                .WithValueField(0, 32, out responseFields[3], FieldMode.Read, name: "Card status (CARDSTATUS4)");

            Registers.DataTimer.Define(this)
                .WithValueField(0, 32, name: "Data and R1b busy timeout period (DATATIME)"); // hush

            Registers.DataLength.Define(this)
                .WithValueField(0, 25, out dataLength, name: "Data length value (DATALENGTH)")
                .WithReservedBits(25, 7);

            DataCtrl = Registers.DataCtrl.Define(this)
                .WithFlag(0, name: "Data transfer enable bit (DTEN)") // hush
                .WithFlag(1, out dataDirectionFromCard, name: "Data transfer direction selection (DTDIR)")
                .WithValueField(4, 4, out blockSizeField, name: "Data block size (DBLOCKSIZE)", writeCallback: (_, val) =>
                {
                    if(val == 15)
                    {
                        this.WarningLog("Data block size 15 is reserved");
                    }
                })
                .WithTaggedFlag("Read Wait start (RWSTART)", 8)
                .WithTaggedFlag("Read Wait stop (RWSTOP)", 9)
                .WithTaggedFlag("Read Wait mode (RWMOD)", 10)
                .WithTaggedFlag("SD I/O interrupt enable functions (SDIOEN)", 11);

            Registers.DataCount.Define(this)
                .WithTag("Data count value (DATACOUNT)", 0, 25)
                .WithReservedBits(25, 7);

            Registers.IntStatus.Define(this, resetValue: 0x90000)
                .WithFlag(0, FieldMode.Read, name: "Command response received (CRC check failed) (CCRCFAIL)", valueProviderCallback: _ => false)
                .WithFlag(1, FieldMode.Read, name: "Data block sent/received (CRC check failed) (DCRCFAIL)", valueProviderCallback: _ => false)
                .WithFlag(2, out cTimeout, FieldMode.Read, name: "Command response timeout (CTIMEOUT)")
                .WithTaggedFlag("Data timeout (DTIMEOUT)", 3)
                .WithFlag(4, out txUnderrun, FieldMode.Read, name: "Transmit FIFO underrun error (TXUNDERR)")
                .WithFlag(5, out rxOverrun, FieldMode.Read, name: "Received FIFO overrun error (RXOVERR)")
                .WithFlag(6, out cmdREnd, FieldMode.Read, name: "Command response received (CRC check passed, or no CRC) (CMDREND)")
                .WithFlag(7, out cmdSent, FieldMode.Read, name: "Command sent (no response required) (CMDSENT)")
                .WithFlag(8, out dataEnd, FieldMode.Read, name: "Data transfer ended correctly (DATAEND)")
                .WithTaggedFlag("Data transfer Hold (DHOLD)", 9)
                .WithFlag(10, out dBckEnd, FieldMode.Read, name: "Data block sent/received (DBCKEND)")
                .WithTaggedFlag("Data transfer aborted by CMD12 (DABORT)", 11)
                .WithFlag(12, FieldMode.Read, name: "Data path state machine active (DPSMACT)", valueProviderCallback: _ => idmaActive)
                .WithFlag(13, FieldMode.Read, name: "Command path state machine active (CPSMACT)", valueProviderCallback: _ => false)
                .WithFlag(14, out txFifoHE, FieldMode.Read, name: "Transmit FIFO half empty: at least 8 words can be written into the FIFO (TXFIFOHE)")
                .WithFlag(15, out rxFifoHF, FieldMode.Read, name: "Receive FIFO half full: there are at least 8 words in the FIFO (RXFIFOHF)")
                .WithFlag(16, out txFifoF, FieldMode.Read, name: "Transmit FIFO full (TXFIFOF)")
                .WithTaggedFlag("Receive FIFO full (RXFIFOF)", 17)
                .WithTaggedFlag("Transmit FIFO empty (TXFIFOE)", 18)
                .WithFlag(19, out rxFifoE, FieldMode.Read, name: "Receive FIFO empty (RXFIFOE)")
                .WithFlag(20, FieldMode.Read, name: "Inverted value of SDMMC_D0 line (Busy) (BUSYD0)", valueProviderCallback: _ => false)
                .WithTaggedFlag("End of SDMMC_D0 Busy following a CMD response detected (BUSYD0END)", 21)
                .WithTaggedFlag("SDIO interrupt received (SDIOT)", 22)
                .WithTaggedFlag("Boot acknowledgment received (boot acknowledgment check fail) (ACKFAIL)", 23)
                .WithTaggedFlag("Boot acknowledgment timeout (ACKTIMEOUT)", 24)
                .WithTaggedFlag("Voltage switch critical timing section completion (VSWEND)", 25)
                .WithTaggedFlag("SDMMC_CK stopped in Voltage switch procedure (CKSTOP)", 26)
                .WithFlag(27, out idmaTransferError, FieldMode.Read, name: "IDMA transfer error (IDMATE)")
                .WithFlag(28, out idmaBufferComplete, FieldMode.Read, name: "IDMA buffer transfer complete (IDMABTC)")
                .WithReservedBits(29, 3);

            Func<IFlagRegisterField, Action<bool, bool>> clearCb = (IFlagRegisterField field) => (_, value) =>
            {
                if(value)
                {
                    field.Value = false;
                }
            };

            IntClear = Registers.IntClear.Define(this)
                .WithFlag(0, name: "CCRFAIL flag clear bit (CCRFAILC)") // hush
                .WithFlag(1, name: "DCRCFAIL flag clear bit (DCRCFAILC)") // hush
                .WithFlag(2, FieldMode.WriteToClear, name: "CTIMEOUT flag clear bit (CTIMEOUTC)", writeCallback: clearCb(cTimeout))
                .WithFlag(3, name: "DTIMEOUT flag clear bit (DTIMEOUTC)") // hush
                .WithFlag(4, FieldMode.WriteToClear, name: "TXUNDERR flag clear bit (TXUNDERRC)", writeCallback: clearCb(txUnderrun))
                .WithFlag(5, FieldMode.WriteToClear, name: "RXOVERR flag clear bit (RXOVERRC)", writeCallback: clearCb(rxOverrun))
                .WithFlag(6, FieldMode.WriteToClear, name: "CMDREND flag clear bit (CMDRENDC)", writeCallback: clearCb(cmdREnd))
                .WithFlag(7, FieldMode.WriteToClear, name: "CMDSENT flag clear bit (CMDSENTC)", writeCallback: clearCb(cmdSent))
                .WithFlag(8, FieldMode.WriteToClear, name: "DATAEND flag clear bit (DATAENDC)", writeCallback: clearCb(dataEnd))
                .WithFlag(9, name: "DHOLD flag clear bit (DHOLDC)") // hush
                .WithFlag(10, FieldMode.WriteToClear, name: "DBCKEN flag clear bit (DBCKEND)", writeCallback: clearCb(dBckEnd))
                .WithFlag(11, name: "DABORT flag clear bit (DABORTC)") // hush
                .WithReservedBits(12, 9)
                .WithFlag(21, name: "BUSYD0END flag clear bit (BUSYD0ENDC)") // hush
                .WithTaggedFlag("SDIOIT flag clear bit (SDIOITC)", 22)
                .WithTaggedFlag("ACKFAIL flag clear bit (ACKFAILC)", 23)
                .WithTaggedFlag("ACKTIMEOUT flag clear bit (ACKTIMEOUTC)", 24)
                .WithTaggedFlag("VSWEND flag clear bit (VSWENDC)", 25)
                .WithTaggedFlag("CKSTOP flag clear bit (CKSTOPC)", 26)
                .WithFlag(27, FieldMode.WriteToClear, name: "IDMATE flag clear bit (IDMATEC)", writeCallback: clearCb(idmaTransferError))
                .WithFlag(28, FieldMode.WriteToClear, name: "IDMABTC flag clear bit (IDMABTCC)", writeCallback: clearCb(idmaBufferComplete))
                .WithWriteCallback((_, __) => UpdateInterrupts());

            IntMask = Registers.IntMask.Define(this)
                .WithTaggedFlag("Command response received (CRC check failed) interrupt enable (CCRCFAILIE)", 0)
                .WithFlag(1, name: "Data block sent/received (CRC check failed) interrupt enable (DCRCFAILIE)") // hush
                .WithFlag(2, out cTimeoutEn, name: "Command response timeout interrupt enable (CTIMEOUTIE)")
                .WithFlag(3, name: "Data timeout interrupt enable (DTIMEOUTIE)") // hush
                .WithFlag(4, out txUnderrunEn, name: "Transmit FIFO underrun error interrupt enable (TXUNDERRIE)")
                .WithFlag(5, out rxOverrunEn, name: "Received FIFO overrun error interrupt enable (RXOVERRIE)")
                .WithFlag(6, out cmdREndEn, name: "Command response received (CRC check passed, or no CRC) interrupt enable (CMDRENDIE)")
                .WithFlag(7, out cmdSentEn, name: "Command sent (no response required) interrupt enable (CMDSENTIE)")
                .WithFlag(8, out dataEndEn, name: "Data transfer ended correctly interrupt enable (DATAENDIE)")
                .WithTaggedFlag("Data transfer Hold interrupt enable (DHOLDIE)", 9)
                .WithFlag(10, out dBckEndEn, name: "Data block sent/received interrupt enable (DBCKENDIE)")
                .WithTaggedFlag("Data transfer aborted interrupt enable (DABORTIE)", 11)
                .WithReservedBits(12, 2)
                .WithFlag(14, out txFifoHEEn, name: "Transmit FIFO half empty: at least 8 words can be written into the FIFO interrupt enable (TXFIFOHEIE)")
                .WithFlag(15, out rxFifoHFEn, name: "Receive FIFO half full: there are at least 8 words in the FIFO interrupt enable (RXFIFOHFIE)")
                .WithReservedBits(16, 1)
                .WithTaggedFlag("Receive FIFO full interrupt enable (RXFIFOFIE)", 17)
                .WithTaggedFlag("Transmit FIFO empty interrupt enable (TXFIFOEIE)", 18)
                .WithReservedBits(19, 2)
                .WithFlag(21, name: "BUSYD0END interrupt enable (BUSYD0ENDIE)") // hush
                .WithTaggedFlag("SDIO interrupt received interrupt enable (SDIOITIE)", 22)
                .WithTaggedFlag("Boot acknowledgment received (boot acknowledgment check fail) interrupt enable (ACKFAILIE)", 23)
                .WithTaggedFlag("Boot acknowledgment timeout interrupt enable (ACKTIMEOUTIE)", 24)
                .WithTaggedFlag("Voltage switch critical timing section completion interrupt enable (VSWENDIE)", 25)
                .WithTaggedFlag("SDMMC_CK stopped in Voltage switch procedure interrupt enable (CKSTOPIE)", 26)
                .WithReservedBits(27, 1)
                .WithFlag(28, out idmaBufferCompleteEn, name: "IDMA buffer transfer complete interrupt enable (IDMABTCIE)")
                .WithReservedBits(29, 3)
                .WithWriteCallback((_, __) => UpdateInterrupts());

            Registers.Fifo.Define(this)
                .WithValueField(0, 32, name: "Receive and transmit FIFO data (FIFODATA)", valueProviderCallback: _ => ReadBuffer(), writeCallback: (_, value) => WriteBuffer((uint)value));

            Clock
                .WithReservedBits(10, 2)
                .WithTaggedFlag("Power saving configuration bit (PWRSAV)", 12)
                .WithReservedBits(13, 1)
                .WithValueField(14, 2, out wideBus, name: "Wide bus mode enable bit (WIDBUS)")
                .WithFlag(16, name: "SDMMC_CK dephasing selection bit for data and command (NEGEDGE)") // hush
                .WithTaggedFlag("Hardware flow control enable (HWFC_EN)", 17)
                .WithFlag(18, out doubleDataRate, name: "Data rate signaling selection (DDR)")
                .WithTaggedFlag("Bus speed for selection of SDMMC operating modes (BUSSPEED)", 19)
                .WithTag("Receive clock selection (SELCLKRX)", 20, 2)
                .WithReservedBits(22, 10);
            Cmd
                .WithFlag(6, name: "The CPSM treats the command as a data transfer command, stops the interrupt period, and signals DataEnable to the DPSM (CMDTRANS)") // hush
                .WithFlag(7, out commandStop, name: "The CPSM treats the command as a Stop Transmission command and signals abort to the DPSM (CMDSTOP)")
                .WithTaggedFlag("Hold new data block transmission and reception in the DPSM (DTHOLD)", 13)
                .WithTaggedFlag("Select the boot mode procedure to be used (BOOTMODE)", 14)
                .WithTaggedFlag("Enable boot mode procedure (BOOTEN)", 15)
                .WithTaggedFlag("The CPSM treats the command as a Suspend or Resume command and signals interrupt period start/end (CMDSUSPEND)", 16)
                .WithReservedBits(17, 15);
            DataCtrl
                .WithTag("Data transfer mode selection (DTMODE)", 2, 2)
                .WithTaggedFlag("Enable the reception of the boot acknowledgment (BOOTACKEN)", 12)
                .WithFlag(13, name: "FIFO reset, flushes any remaining data (FIFORST)") // hush
                .WithReservedBits(14, 18);

            for(long offset = 0x4; offset < 0x40; offset += 0x4)
            {
                RegistersCollection.DefineRegister((long)Registers.Fifo + offset)
                    .WithTag("Receive and transmit FIFO data (FIFODATA)", 0, 32);
            }
            Registers.IDMACtrl.Define(this)
                .WithFlag(0, out idmaEnabled, name: "IDMA enable (IDMAEN)")
                .WithFlag(1, out idmaLinkedList, name: "Buffer mode selection (IDMABMODE)")
                .WithReservedBits(2, 30);
            Registers.IDMABSize.Define(this)
                .WithReservedBits(0, 5)
                .WithValueField(5, 12, out idmaBufferSize, name: "Number of bytes per buffer (IDMABNDT)")
                .WithReservedBits(17, 15);
            Registers.IDMABase.Define(this)
                .WithValueField(0, 32, out idmaBufferBase, name: "Buffer memory base address (IDMABASE)");
            // RM0456 31.10.18: IDMALA[13:0] in bits 15:2, a word offset (cmsis-device-u5 masks
            // bits 13:0; the HAL writes byte offsets of word aligned items, the same bits).
            Registers.IDMALinkedListAddress.Define(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 14, out idmaItemOffset, name: "Word aligned linked list item address offset (IDMALA)")
                .WithReservedBits(16, 13)
                .WithFlag(29, out idmaBufferReady, name: "Acknowledge linked list buffer ready (ABR)")
                .WithFlag(30, out idmaUpdateSize, name: "Update IDMABSIZE at the next linked list load (ULS)")
                .WithFlag(31, out idmaUpdateAddress, name: "Update IDMALAR and IDMABASE at the end of the buffer (ULA)");
            Registers.IDMALinkedListBase.Define(this)
                .WithReservedBits(0, 2)
                .WithValueField(2, 30, out idmaListBase, name: "Word aligned linked list memory base address (IDMABA)");
        }

        protected uint ReadBuffer()
        {
            var internalBytes = ReadDataBuffer.DequeueRange(4);
            var val = internalBytes.ToUInt32Smart();
            if(ReadDataBuffer.Count == 0)
            {
                rxFifoHF.Value = false;
                rxFifoE.Value = true;
                dataEnd.Value = true;
                dBckEnd.Value = true;
                UpdateInterrupts();
            }
            return val;
        }

        protected void WriteBuffer(uint data)
        {
            var sdCard = RegisteredPeripheral;
            if(sdCard == null)
            {
                this.WarningLog("Tried to write to SD card, but no SD card is currently attached");
                return;
            }
            WriteCard(sdCard, data);
            WriteDataLeft -= 4;
            this.DebugLog("Remaining data to write {0}", WriteDataLeft);

            if(WriteDataLeft == 0)
            {
                txFifoHE.Value = false;
                txFifoF.Value = true;
                dataEnd.Value = true;
                dBckEnd.Value = true;
                UpdateInterrupts();
            }
        }

        protected ulong WriteDataLeft { get; private set; }

        private int ClkDivWidth { get => Stm32HClkDivWidth; }

        private int CommandFieldsOffset { get => Stm32HCommandFieldsOffset; }

        protected DoubleWordRegister Clock;
        protected DoubleWordRegister Cmd;
        protected DoubleWordRegister DataCtrl;

        protected readonly Queue<byte> ReadDataBuffer;

        private void ProcessRead(STM32U5_SDCard sdCard, uint length = uint.MaxValue, bool ignoreBlockSize = false)
        {
            if(!dataDirectionFromCard.Value)
            {
                this.WarningLog("Read transfer while DTDIR is set");
            }
            ReadCard(sdCard, ignoreBlockSize ? length : Math.Min(BlockSize, length));
            rxFifoHF.Value = true;
            rxFifoE.Value = false;
            UpdateInterrupts();
        }

        private void ProcessWrite(uint length = uint.MaxValue, bool ignoreBlockSize = false)
        {
            if(dataDirectionFromCard.Value)
            {
                this.WarningLog("Write transfer while DTDIR is clear");
            }
            WriteDataLeft = ignoreBlockSize ? length : Math.Min(BlockSize, length);
            txFifoHE.Value = true;
            txFifoF.Value = false;
            UpdateInterrupts();
        }

        private void ProcessDataCommand(STM32U5_SDCard sdCard, SDCardCommand command)
        {
            if(idmaEnabled.Value)
            {
                StartIdmaTransfer(sdCard, command);
                return;
            }
            switch(command)
            {
            case SDCardCommand.CheckSwitchableFunction_CMD6:
                ProcessRead(sdCard, SwitchFunctionStatusLength);
                break;
            case SDCardCommand.ReadSingleBlock_CMD17:
                ProcessRead(sdCard);
                break;
            case SDCardCommand.ReadMultipleBlocks_CMD18:
                ProcessRead(sdCard, (uint)dataLength.Value, true);
                break;
            case SDCardCommand.WriteSingleBlock_CMD24:
                ProcessWrite();
                break;
            case SDCardCommand.WriteMultipleBlocks_CMD25:
                ProcessWrite((uint)dataLength.Value, true);
                break;
            case (SDCardCommand)SDCardAppCommand.SendSDCardStatus_ACMD13:
                ProcessRead(sdCard, 64);
                break;
            case (SDCardCommand)SDCardAppCommand.SendSDConfigurationRegister_ACMD51:
                ProcessRead(sdCard, 8);
                break;
            }
        }

        private void ProcessCommand(STM32U5_SDCard sdCard, SDCardCommand command, bool wasAppCommand)
        {
            switch(command)
            {
            case SDCardCommand.GoIdleState_CMD0:
                GoIdle();
                break;
            case SDCardCommand.CheckSwitchableFunction_CMD6:
                // CMD6 (SWITCH_FUNC) answers with the 64-byte switch function status on the data
                // lines; ACMD6 (SET_BUS_WIDTH) has no data.
                if(wasAppCommand)
                {
                    break;
                }
                switchFunctionStatusPending = true;
                goto case SDCardCommand.ReadSingleBlock_CMD17;
            case SDCardCommand.SendStatus_CMD13:
                // CMD13 sends data only when it is ACMD13 (ie. was treated as an application command)
                if(wasAppCommand) goto case SDCardCommand.ReadSingleBlock_CMD17;
                break;
            case SDCardCommand.ReadSingleBlock_CMD17:
            case SDCardCommand.ReadMultipleBlocks_CMD18:
            case SDCardCommand.WriteSingleBlock_CMD24:
            case SDCardCommand.WriteMultipleBlocks_CMD25:
            case (SDCardCommand)SDCardAppCommand.SendSDConfigurationRegister_ACMD51:
                // Need to delay the data so that firmware never consumes all data before reading the response
                Machine.LocalTimeSource.ExecuteInNearestSyncedState(_ => ProcessDataCommand(sdCard, command));
                break;
            case SDCardCommand.SendCardIdentification_CMD2:
            case SDCardCommand.SendRelativeAddress_CMD3:
            case SDCardCommand.SendCardSpecificData_CMD9:
            case SDCardCommand.SelectDeselectCard_CMD7:
                break;
            case SDCardCommand.StopTransmission_CMD12:
                // CMDSTOP: the DPSM transfer is aborted.
                if(commandStop.Value)
                {
                    StopIdmaTransfer();
                }
                break;
            case SDCardCommand.SetBlockLength_CMD16:
            case SDCardCommand.EraseWriteBlockStart_CMD32:
            case SDCardCommand.EraseWriteBlockEnd_CMD33:
            case SDCardCommand.Erase_CMD38:
            case SDCardCommand.SendInterfaceConditionCommand_CMD8:
            case SDCardCommand.AppCommand_CMD55:
            case (SDCardCommand)SDCardAppCommand.SendOperatingConditionRegister_ACMD41:
                break;
            default:
                this.WarningLog("Calling command without explicit handling with index: {0}", command);
                break;
            }
        }

        private void UpdateInterrupts()
        {
            IRQ.Set(
                   (txFifoHE.Value && txFifoHEEn.Value)
                || (rxFifoHF.Value && rxFifoHFEn.Value)
                || (txUnderrun.Value && txUnderrunEn.Value)
                || (rxOverrun.Value && rxOverrunEn.Value)
                || (idmaBufferComplete.Value && idmaBufferCompleteEn.Value)
                || (dataEnd.Value && dataEndEn.Value)
                || (cmdSent.Value && cmdSentEn.Value)
                || (cmdREnd.Value && cmdREndEn.Value)
                || (dBckEnd.Value && dBckEndEn.Value)
                || (cTimeout.Value && cTimeoutEn.Value)
            );
        }

        private void GoIdle()
        {
            StopIdmaTransfer();
            switchFunctionStatusPending = false;
            RegisteredPeripheral?.Reset();
            ReadDataBuffer.Clear();
            WriteDataLeft = 0;
            rxFifoE.Value = true;
            rxFifoHF.Value = false;
            txFifoF.Value = true;
            txFifoHE.Value = false;
            UpdateInterrupts();
        }

        private byte[] ReadCardData(STM32U5_SDCard sdCard, uint size)
        {
            if(!switchFunctionStatusPending)
            {
                return sdCard.ReadData(size);
            }
            // The status is sent most significant byte first, as the other card registers.
            switchFunctionStatusPending = false;
            var status = sdCard.ReadSwitchFunctionStatusRegister().Reverse().ToArray();
            var result = new byte[size];
            Array.Copy(status, result, Math.Min(status.Length, (int)size));
            return result;
        }

        private void StartIdmaTransfer(STM32U5_SDCard sdCard, SDCardCommand command)
        {
            switch(command)
            {
            case SDCardCommand.ReadSingleBlock_CMD17:
            case SDCardCommand.ReadMultipleBlocks_CMD18:
            case SDCardCommand.CheckSwitchableFunction_CMD6:
            case (SDCardCommand)SDCardAppCommand.SendSDCardStatus_ACMD13:
            case (SDCardCommand)SDCardAppCommand.SendSDConfigurationRegister_ACMD51:
                idmaFromCard = true;
                break;
            case SDCardCommand.WriteSingleBlock_CMD24:
            case SDCardCommand.WriteMultipleBlocks_CMD25:
                idmaFromCard = false;
                break;
            default:
                return;
            }
            if(idmaFromCard != dataDirectionFromCard.Value)
            {
                this.WarningLog("IDMA transfer of {0} with DTDIR {1}", command, dataDirectionFromCard.Value ? "from card" : "to card");
            }
            idmaCard = sdCard;
            idmaRemaining = (uint)dataLength.Value;
            idmaBufferAddress = (uint)idmaBufferBase.Value;
            idmaActive = true;
            StartIdmaBuffer();
        }

        // Runs the current buffer for the time its bytes take on the SDMMC bus.
        private void StartIdmaBuffer()
        {
            idmaChunk = idmaRemaining;
            if(idmaLinkedList.Value)
            {
                idmaChunk = Math.Min(idmaChunk, (uint)idmaBufferSize.Value * IdmaBufferSizeUnit);
            }
            if(idmaChunk == 0)
            {
                IdmaTransferError("empty buffer");
                return;
            }
            var lines = wideBus.Value == 0 ? 1u : wideBus.Value == 1 ? 4u : 8u;
            var divider = clockDivider.Value == 0 ? 1ul : 2 * clockDivider.Value;
            var cycles = (ulong)idmaChunk * 8 / lines * divider;
            if(doubleDataRate.Value)
            {
                cycles /= 2;
            }
            idmaTimer.Enabled = false;
            idmaTimer.Limit = Math.Max(1, cycles);
            idmaTimer.ResetValue();
            idmaTimer.Enabled = true;
        }

        private void FinishIdmaBuffer()
        {
            if(!idmaActive)
            {
                return;
            }
            if(idmaFromCard)
            {
                sysbus.WriteBytes(ReadCardData(idmaCard, idmaChunk), idmaBufferAddress);
            }
            else
            {
                idmaCard.WriteData(sysbus.ReadBytes(idmaBufferAddress, (int)idmaChunk));
            }
            idmaRemaining -= idmaChunk;

            if(idmaLinkedList.Value && idmaUpdateAddress.Value && idmaChunk == idmaBufferSize.Value * IdmaBufferSizeUnit)
            {
                idmaBufferComplete.Value = true;
            }
            if(idmaRemaining == 0)
            {
                idmaActive = false;
                dataEnd.Value = true;
                dBckEnd.Value = true;
                UpdateInterrupts();
                return;
            }
            if(!idmaLinkedList.Value)
            {
                idmaBufferAddress += idmaChunk;
                StartIdmaBuffer();
                return;
            }
            if(!idmaUpdateAddress.Value)
            {
                IdmaTransferError("the linked list ended before DATALENGTH");
                return;
            }
            // Next linked list item: IDMALAR, IDMABASER and, when the current ULS is set, IDMABSIZER.
            var item = ((ulong)idmaListBase.Value << 2) + ((ulong)idmaItemOffset.Value << 2);
            var loadSize = idmaUpdateSize.Value;
            RegistersCollection.Write((long)Registers.IDMALinkedListAddress, sysbus.ReadDoubleWord(item));
            RegistersCollection.Write((long)Registers.IDMABase, sysbus.ReadDoubleWord(item + 4));
            if(loadSize)
            {
                RegistersCollection.Write((long)Registers.IDMABSize, sysbus.ReadDoubleWord(item + 8));
            }
            UpdateInterrupts();
            if(!idmaBufferReady.Value)
            {
                IdmaTransferError("the linked list buffer is not acknowledged (ABR = 0)");
                return;
            }
            idmaBufferAddress = (uint)idmaBufferBase.Value;
            StartIdmaBuffer();
        }

        // RM0456 31.5.6: an IDMA transfer error stops the transfer and normally causes
        // RXOVERR or TXUNDERR.
        private void IdmaTransferError(string reason)
        {
            this.WarningLog("IDMA transfer error: {0}", reason);
            idmaActive = false;
            idmaTimer.Enabled = false;
            idmaTransferError.Value = true;
            if(idmaFromCard)
            {
                rxOverrun.Value = true;
            }
            else
            {
                txUnderrun.Value = true;
            }
            UpdateInterrupts();
        }

        private void StopIdmaTransfer()
        {
            idmaActive = false;
            idmaTimer.Enabled = false;
        }

        private uint BlockSize { get => 1u << (byte)blockSizeField.Value; }

        private long kernelClockFrequency;
        private bool switchFunctionStatusPending;
        private bool idmaActive;
        private bool idmaFromCard;
        private uint idmaRemaining;
        private uint idmaChunk;
        private uint idmaBufferAddress;
        private STM32U5_SDCard idmaCard;

        private IValueRegisterField dataLength;
        private IValueRegisterField blockSizeField;
        private IFlagRegisterField dataDirectionFromCard;
        private IValueRegisterField clockDivider;
        private IValueRegisterField wideBus;
        private IFlagRegisterField doubleDataRate;
        private IFlagRegisterField commandStop;

        private IFlagRegisterField idmaEnabled;
        private IFlagRegisterField idmaLinkedList;
        private IValueRegisterField idmaBufferSize;
        private IValueRegisterField idmaBufferBase;
        private IValueRegisterField idmaItemOffset;
        private IFlagRegisterField idmaBufferReady;
        private IFlagRegisterField idmaUpdateSize;
        private IFlagRegisterField idmaUpdateAddress;
        private IValueRegisterField idmaListBase;

        private IFlagRegisterField txFifoHE;
        private IFlagRegisterField txFifoF;
        private IFlagRegisterField rxFifoHF;
        private IFlagRegisterField rxFifoE;
        private IFlagRegisterField dataEnd;
        private IFlagRegisterField cmdSent;
        private IFlagRegisterField cmdREnd;
        private IFlagRegisterField dBckEnd;
        private IFlagRegisterField cTimeout;
        private IFlagRegisterField txUnderrun;
        private IFlagRegisterField rxOverrun;
        private IFlagRegisterField idmaTransferError;
        private IFlagRegisterField idmaBufferComplete;

        private IFlagRegisterField txFifoHEEn;
        private IFlagRegisterField rxFifoHFEn;
        private IFlagRegisterField txUnderrunEn;
        private IFlagRegisterField rxOverrunEn;
        private IFlagRegisterField idmaBufferCompleteEn;
        private IFlagRegisterField dataEndEn;
        private IFlagRegisterField cmdSentEn;
        private IFlagRegisterField cmdREndEn;
        private IFlagRegisterField dBckEndEn;
        private IFlagRegisterField cTimeoutEn;

        private DoubleWordRegister IntClear;
        private DoubleWordRegister IntMask;

        private readonly IValueRegisterField[] responseFields;
        private readonly IBusController sysbus;
        private readonly LimitTimer idmaTimer;

        private const long DefaultKernelClockFrequency = 48000000;
        private const uint IdmaBufferSizeUnit = 32;
        private const uint SwitchFunctionStatusLength = 64;
        private const int Stm32HClkDivWidth = 10;
        private const int Stm32HCommandFieldsOffset = 8;

        private enum Registers
        {
            Power = 0x00,
            Clock = 0x04,
            Arg = 0x08,
            Cmd = 0x0C,
            RespCmd = 0x10,
            Resp1 = 0x14,
            Resp2 = 0x18,
            Resp3 = 0x1c,
            Resp4 = 0x20,
            DataTimer = 0x24,
            DataLength = 0x28,
            DataCtrl = 0x2C,
            DataCount = 0x30,
            IntStatus = 0x34,
            IntClear = 0x38,
            IntMask = 0x3C,
            IDMACtrl = 0x50,
            IDMABSize = 0x54,
            IDMABase = 0x58,
            IDMALinkedListAddress = 0x64,
            IDMALinkedListBase = 0x68,
            Fifo = 0x80
        }

        private enum ResponseWidth
        {
            NoResponse = 0,
            ShortResponse = 1,
            ShortResponseNoCrc = 2,
            LongResponse = 3
        }
    }
}
