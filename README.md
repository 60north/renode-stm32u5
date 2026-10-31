# STM32U5 support for Renode

Platform descriptions, peripheral models and tests for running STM32U575 firmware in
[Renode](https://renode.io) 1.17.0, with TrustZone disabled (TZEN = 0) or enabled (TZEN = 1).

## What is modelled

Every STM32U575 peripheral is modelled except the ones listed under
[Not modelled](#not-modelled): the Cortex-M33 core with the NVIC, SysTick and DWT; flash,
SRAM1-4 and BKPSRAM; RCC, PWR, flash interface, SYSCFG, ICACHE, DCACHE1, DBGMCU; GPIO ports A-I,
LPGPIO1, EXTI; GPDMA1 and LPDMA1; USART1-3, UART4/5, LPUART1; SPI1-3; I2C1-4; FDCAN1; OCTOSPI1/2
with OCTOSPIM; SDMMC1/2 with an SD card; FMC; timers TIM1-8 and TIM15-17; RTC and TAMP; IWDG;
ADC1; RNG; HASH. With TZEN = 1: the IDAU, secure aliases of memories and peripherals, the GTZC
(TZSC firewall of the securable peripherals, TZIC, MPCBB1-4 for the SRAMs, MPCWM1-5 with
BKPSRAM behind MPCWM4) and the secure registers of the TrustZone-aware peripherals (RCC, PWR,
GPIO, EXTI, FLASH, GPDMA, RTC, TAMP, SYSCFG). Each model file names the Renode model it was
adapted from and what it does not model.

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

Peripherals: ADC4, DAC1, COMP1/2, OPAMP1/2, VREFBUF, LPTIM1-4, WWDG, SAI1/2, MDF1, ADF1,
DCMI, PSSI, DMA2D, CRC, CORDIC, FMAC, USB OTG FS, UCPD1, CRS, TSC, RAMCFG. Accesses to them are
logged as accesses to non-existing peripherals.

Behaviour of the modelled peripherals:

- Low-power modes. Timing of oscillators, PLLs, voltage scaling and flash erase: they complete
  at once. SysTick always counts HCLK; the DWT cycle counter runs at the reset clock.
- Analog behaviour and error conditions of the communication peripherals: USART overrun and
  reception errors, SPI slave mode and CRC, I2C bus timing and errors, CAN arbitration,
  acknowledgement and bus errors, SD card CRC and timeouts. FDCAN frames take their time at the
  nominal bit rate; SDMMC buffers the time of their bytes on the bus.
- GPDMA 2D addressing, triggers, bursts and transmit requests; timer DMA requests; HASH and
  OCTOSPI DMA; OCTOSPI DTR, DQS, HyperBus and wrap modes; RTC shadow registers, timestamp and
  tamper detection; IWDG early wake-up. Timer break, dead-time and encoder modes are not tested.
- The routing of LPGPIO1 to pins (a board connects its outputs) and the inherited LPGPIO
  security; the effect of the SYSCFG CPU and class B locks.
- Flash option byte changes, ECC and write protection. Flash contents survive a machine reset,
  not a restart of Renode.
- TrustZone: TZEN is fixed by the platform. Execute accesses are checked as data accesses;
  accesses without a CPU or DMA context (Monitor, ELF loading) pass the firewalls; SDMMC
  internal DMA transfers do not carry the SDMMC security.

## TrustZone platform speed

`stm32u575-trustzone.repl` runs slower than `stm32u575.repl` in host time: the MPCBB firewall
checks every SRAM access, so the SRAMs are `ArrayMemory` behind the firewall instead of
`MappedMemory`. Emulated time is not affected. Firmware that waits in loops (`HAL_Delay`, flag
polling) suffers most; lowering the instruction rate with `cpu PerformanceInMips 10` makes those
loops run fewer iterations for the same delay (timers and peripheral timing do not change). The
ST TrustZone tests run at 10 MIPS.

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
