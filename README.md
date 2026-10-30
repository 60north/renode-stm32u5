# STM32U5 support for Renode

Platform description, peripheral models and tests for running STM32U575 firmware in
[Renode](https://renode.io) 1.17.0.

## Contents

- `platforms/cpus/stm32u575.repl` - STM32U575 platform; `platforms/boards/` - NUCLEO-U575ZI-Q and
  STM32U575I-EV (LEDs and user button).
- `peripherals/` - peripheral models: RCC, PWR, flash interface (with programming checks),
  ICACHE, GPIO, EXTI, USART (with idle line detection), SPI, I2C, FDCAN (Classic CAN timing, Tx
  and Rx FIFOs, CAN hub), GPDMA (with linked-list mode), timers TIM1-8 and TIM15-17, RTC with the
  TAMP backup registers, IWDG, ADC1 and DBGMCU, and an SD card for the SPI mode. Each file names
  the Renode model it was adapted from.
- `scripts/load-models.resc` - compiles the models in Renode (`include`), before the platform is
  loaded.
- `firmware-tests/` - test firmware (CMake): bare-metal programs, programs using the STM32CubeU5
  HAL and official STM32CubeU5 example projects. The STMicroelectronics sources are downloaded
  at configure time at the commits listed in `firmware-tests/cmake/`.
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

An SD card in SPI mode is attached to an SPI controller, and its chip select pin to the
controller's chip select input. With software slave select (`SPI_NSS_SOFT`, SSM = 1) the card
keeps its command state while the pin is low and drops it when the pin goes high:

```text
sdCard: SD.STM32U5_SDCard @ spi1
    imageFile: "path/to/sd.img"
    capacity: 0x100000000
    spiMode: true
    persistent: true

gpioPortD:
    2 -> spi1@0
```

Inputs are driven from the Monitor, for example `sysbus.gpioPortC OnGPIO 13 true` for a pin and
`sysbus.adc1.adc1-channel4 Volts 1.65` for an ADC input. FDCAN instances of several machines
exchange frames through a CAN hub (`emulation CreateCANHub "can"`,
`connector Connect sysbus.fdcan1 can`); see `tests/integration/stm32u575-hal-fdcan-nodes.robot`.

## Not modelled

- TrustZone (secure state, secure aliases, GTZC), low-power modes and the peripherals that are
  not listed above, for example ADC4, LPTIM, DAC, OCTOSPI, SDMMC, USB, CRC and RNG.
- Timing of oscillators, PLLs, voltage scaling and flash erase: they complete at once. SysTick
  always counts HCLK.
- Analog behaviour and error conditions of the communication peripherals: USART overrun and
  reception errors, SPI slave mode and CRC, I2C bus timing and errors, CAN arbitration,
  acknowledgement and bus errors. FDCAN frames take their time at the nominal bit rate.
- GPDMA 2D addressing, triggers, bursts and transmit requests; timer DMA requests; RTC shadow
  registers, timestamp and tamper detection; IWDG early wake-up. Timer break, dead-time and
  encoder modes are not tested.
- Flash option byte changes, ECC and write protection. Flash contents survive a machine reset,
  not a restart of Renode.

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
