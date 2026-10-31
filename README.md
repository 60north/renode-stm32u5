# STM32U5 support for Renode

Platform descriptions, peripheral models and tests for running STM32U575 firmware in
[Renode](https://renode.io) 1.17.0, with TrustZone disabled (TZEN = 0) or enabled (TZEN = 1).

## What is modelled

Every STM32U575 peripheral is modelled except the ones listed under
[Not modelled](#not-modelled).

| Area | Modelled |
| --- | --- |
| Core | Cortex-M33, NVIC, SysTick, DWT |
| Memories | flash, SRAM1-4, BKPSRAM |
| System | RCC, PWR, flash interface, SYSCFG, ICACHE, DCACHE1, DBGMCU |
| I/O | GPIO ports A-I, LPGPIO1, EXTI |
| DMA | GPDMA1, LPDMA1 |
| Communication | USART1-3, UART4/5, LPUART1, SPI1-3, I2C1-4, FDCAN1 |
| Storage | OCTOSPI1/2 with OCTOSPIM, SDMMC1/2 with an SD card, FMC |
| Timers | TIM1-8, TIM15-17, RTC with TAMP, IWDG |
| Analog | ADC1 |
| Security | RNG, HASH |

With TrustZone enabled (TZEN = 1):

- the IDAU and the secure aliases of memories and peripherals;
- the GTZC: TZSC firewall of the securable peripherals, TZIC, MPCBB1-4 for the SRAMs, MPCWM1-5
  (BKPSRAM behind MPCWM4);
- the secure registers of the TrustZone-aware peripherals: RCC, PWR, GPIO, EXTI, FLASH, GPDMA,
  RTC, TAMP, SYSCFG.

Each model file names the Renode model it was adapted from and what it does not model.

The tests run unmodified STM32CubeU5 HAL firmware and official STM32CubeU5 example projects,
among them SD_ReadWrite_DMALinkedList (SDMMC with the internal DMA), HASH_HMAC_SHA1MD5, RNG,
DMA, FDCAN, and six TrustZone projects including the ThreadX application
Tx_SecureLEDToggle_TrustZone.

## Contents

- `platforms/cpus/stm32u575.repl` - STM32U575, TZEN = 0; `stm32u575-trustzone.repl` - TZEN = 1;
  both extend `stm32u575-common.repl`. `platforms/boards/` - NUCLEO-U575ZI-Q (also with
  TrustZone) and STM32U575I-EV (LEDs, user button, the evaluation board SRAM on FMC).
- `peripherals/` - the peripheral models.
- `scripts/load-models.resc` - compiles the models in Renode (`include`), before a platform is
  loaded; `scripts/stm32u575.resc` - runs `$bin` on the STM32U575 platform.
- `firmware-tests/` - test firmware (CMake): bare-metal programs (also a secure and a non-secure
  TrustZone image), programs using the STM32CubeU5 HAL and official STM32CubeU5 example projects.
  The STMicroelectronics sources are downloaded at configure time at the commits listed in
  `firmware-tests/cmake/`.
- `tests/` - Robot Framework tests.

## Running your firmware

In the Renode Monitor, with paths relative to this repository:

```text
include @scripts/load-models.resc
mach create "stm32u575"
machine LoadPlatformDescription @platforms/boards/nucleo_u575zi_q.repl
sysbus LoadELF @path/to/firmware.elf
start
```

`load-models.resc` compiles the models; it must run before a platform that uses them is loaded.
External oscillators exist only when the platform sets them. A board with a 32.768 kHz LSE
crystal, for example, can extend a board file in its own `.repl`:

```text
using "<this repository>/platforms/boards/nucleo_u575zi_q.repl"

rcc:
    lseFrequency: 32768
```

For TrustZone firmware use `platforms/boards/nucleo_u575zi_q-trustzone.repl` and load the
non-secure image before the secure one (the secure image sets the start-up state):

```text
sysbus LoadELF @path/to/nonsecure.elf
sysbus LoadELF @path/to/secure.elf
```

An SD card is attached to SDMMC1 (SD mode, internal DMA) or to an SPI controller (SPI mode). In
SPI mode the card's chip select pin goes to the controller's chip select input: with software
slave select (`SPI_NSS_SOFT`, SSM = 1) the card keeps its command state while the pin is low
and drops it when the pin goes high.

```text
sdCard: SD.STM32U5_SDCard @ sdmmc1
    imageFile: "path/to/sd.img"
    capacity: 0x100000000
    persistent: true

spiCard: SD.STM32U5_SDCard @ spi1
    imageFile: "path/to/other.img"
    capacity: 0x100000000
    spiMode: true

gpioPortD:
    2 -> spi1@0
```

A serial flash is attached to OCTOSPI1 with its memory at the memory-mapped region:

```text
octoFlashMemory: Memory.MappedMemory @ sysbus 0x90000000
    size: 0x1000000

octoFlash: SPI.GenericSpiFlash @ octospi1
    underlyingMemory: octoFlashMemory
    manufacturerId: 0xC2
    memoryType: 0x85
```

Inputs are driven from the Monitor, for example `sysbus.gpioPortC OnGPIO 13 true` for a pin and
`sysbus.adc1.adc1-channel4 Volts 1.65` for an ADC input. FDCAN instances of several machines
exchange frames through a CAN hub (`emulation CreateCANHub "can"`,
`connector Connect sysbus.fdcan1 can`); see `tests/integration/stm32u575-hal-fdcan-nodes.robot`.

## Not modelled

### Peripherals

ADC4, DAC1, COMP1/2, OPAMP1/2, VREFBUF, LPTIM1-4, WWDG, SAI1/2, MDF1, ADF1, DCMI, PSSI, DMA2D,
CRC, CORDIC, FMAC, USB OTG FS, UCPD1, CRS, TSC and RAMCFG have no model. Firmware that accesses
them gets a "non-existing peripheral" warning in the log.

### Limitations of the modelled peripherals

- **Power and clocks.** Low-power modes are not modelled. Oscillators, PLLs and voltage scaling
  become ready immediately. SysTick always counts HCLK. The DWT cycle counter runs at the reset
  clock frequency.
- **Communication.** Error conditions are not simulated: no USART overrun or reception errors,
  no SPI CRC, no I2C bus errors, no CAN arbitration, acknowledgement or bus errors, no SD card
  CRC errors or timeouts. SPI works in master mode only. FDCAN frames and SDMMC data take their
  transmission time on the bus; other transfers complete immediately.
- **DMA.** GPDMA has no 2D addressing, triggers, bursts or transmit requests. Timers, HASH and
  OCTOSPI have no DMA requests.
- **OCTOSPI.** Only single and multi-line SDR transfers; no DTR, DQS, HyperBus or wrap mode.
- **Timers, RTC and watchdog.** The RTC has no shadow registers, timestamp or tamper detection;
  the IWDG has no early wake-up interrupt. Timer break, dead-time and encoder modes are not
  tested.
- **LPGPIO1.** Its I/Os are not routed to the GPIO pins: a board description connects them. It
  does not inherit the GPIO pin security.
- **SYSCFG.** The CPU register locks and the class B locks are stored but have no effect.
- **Flash.** Erase completes immediately. Option bytes cannot be changed at run time; ECC and
  write protection are not modelled. The flash contents survive a machine reset, but not a
  restart of Renode.
- **TrustZone.** TZEN is fixed by the platform description. Instruction fetches are checked like
  data accesses. Accesses without a CPU or DMA context, such as Monitor commands and ELF loading,
  pass the firewalls. SDMMC internal DMA transfers do not carry the SDMMC security attribute.

## TrustZone platform speed

For emulating TrustZone firmware, remember that `stm32u575-trustzone.repl` is much slower in
host time than `stm32u575.repl`. The GTZC block-based firewall (MPCBB) has to check every SRAM
access, so the TrustZone platform uses `ArrayMemory` behind the firewall instead of the faster
`MappedMemory`. Emulated time is not affected, only the time it takes on your computer.

Firmware that waits in loops, for example in `HAL_Delay` or while polling a flag, is affected
most. To speed it up, lower the instruction rate:

```text
cpu PerformanceInMips 10
```

Renode advances emulated time by the number of executed instructions, so a wait loop then runs
fewer iterations for the same delay. Timers, SysTick, the RTC and peripheral timing do not
change. Do not do this if the firmware must finish heavy computation within a time window. The
ST TrustZone tests in this repository run at 10 MIPS.

## Running the tests

Requirements: Arm GNU Toolchain, CMake, Ninja, Git, Python 3.

```sh
curl -sSfL https://builds.renode.io/renode-1.17.0.linux-portable.tar.gz | tar xz
pip install -r renode_1.17.0-portable/tests/requirements.txt
cmake -S firmware-tests -B build/firmware -G Ninja
cmake --build build/firmware
renode_1.17.0-portable/renode-test $(find tests -name '*.robot')
```

The same steps run in GitHub Actions (`.github/workflows/ci.yml`) with Renode 1.17.0 and with the
latest Renode nightly build.

## License

MIT, see `LICENSE`. Models adapted from Renode keep the Antmicro copyright notice.
