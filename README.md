# STM32U5 support for Renode

Platform description, peripheral models and tests for running STM32U575 firmware in
[Renode](https://renode.io) 1.17.0.

## Contents

- `platforms/cpus/stm32u575.repl` - STM32U575 platform; `platforms/boards/` - NUCLEO-U575ZI-Q and
  STM32U575I-EV (LEDs and user button).
- `peripherals/` - peripheral models: RCC, PWR, flash interface, ICACHE, GPIO, EXTI, USART, SPI,
  I2C, FDCAN, GPDMA. Each file names the Renode model it was adapted from.
- `scripts/load-models.resc` - compiles the models in Renode (`include`), before the platform is
  loaded.
- `firmware-tests/` - test firmware (CMake): bare-metal programs, programs using the STM32CubeU5
  HAL and official STM32CubeU5 example projects. The STMicroelectronics sources are downloaded
  at configure time at the commits listed in `firmware-tests/cmake/`.
- `tests/` - Robot Framework tests.

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
