cmake_minimum_required(VERSION 3.20)

if(NOT CMAKE_TOOLCHAIN_FILE)
    set(CMAKE_TOOLCHAIN_FILE ${CMAKE_CURRENT_LIST_DIR}/cmake/arm-none-eabi.cmake)
endif()

project(stm32u5_firmware_tests C ASM)

set(CORTEX_M33_FLAGS -mcpu=cortex-m33 -mthumb -mfloat-abi=soft)

# Bare-metal test firmware: no HAL, no C library start-up code.
function(add_baremetal_firmware name)
    add_executable(${name} ${ARGN} ${CMAKE_CURRENT_LIST_DIR}/baremetal/common/startup.c)
    set_target_properties(${name} PROPERTIES SUFFIX ".elf")
    target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_LIST_DIR}/baremetal/common)
    target_compile_options(${name} PRIVATE ${CORTEX_M33_FLAGS} -std=c11 -O1 -g -Wall -Wextra -Werror
                           -ffunction-sections -fdata-sections)
    target_link_options(${name} PRIVATE ${CORTEX_M33_FLAGS} -nostartfiles -nostdlib
                        -T${CMAKE_CURRENT_LIST_DIR}/baremetal/common/stm32u575xi.ld
                        -Wl,--gc-sections -Wl,-Map=${name}.map)
endfunction()

add_baremetal_firmware(core-smoke baremetal/core/main.c)
add_baremetal_firmware(clock-init baremetal/clock/main.c)

# STM32CubeU5 HAL firmware. Requires network access at configure time.
option(STM32CUBE_FIRMWARE "Build firmware based on the STM32CubeU5 HAL" ON)
if(STM32CUBE_FIRMWARE)
    include(${CMAKE_CURRENT_LIST_DIR}/cmake/stm32cube.cmake)
    add_stm32cube_firmware(hal-clock stm32cube/clock/main.c)
    add_stm32cube_firmware(hal-gpio stm32cube/gpio/main.c)
    add_stm32cube_firmware(hal-uart stm32cube/uart/main.c)
    add_stm32cube_firmware(hal-spi stm32cube/spi/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-spi-dma stm32cube/spi-dma/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-spi-dma-loopback stm32cube/spi-dma-loopback/main.c
        stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-uart-dma stm32cube/uart-dma/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-uart-dma-circular stm32cube/uart-dma-circular/main.c
        stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-rtc stm32cube/rtc/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-adc stm32cube/adc/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-flash stm32cube/flash/main.c stm32cube/common/clock_160mhz.c)
    # Two nodes of one FDCAN firmware for a Renode CAN hub.
    add_stm32cube_firmware(hal-fdcan-tx stm32cube/fdcan-node/main.c stm32cube/common/clock_160mhz.c)
    target_compile_definitions(hal-fdcan-tx PRIVATE NODE_TX)
    add_stm32cube_firmware(hal-fdcan-rx stm32cube/fdcan-node/main.c stm32cube/common/clock_160mhz.c)
    target_compile_definitions(hal-fdcan-rx PRIVATE NODE_RX)
    # Receiver that starts reading only after 20 ms: the Rx FIFO overflows.
    add_stm32cube_firmware(hal-fdcan-rx-slow stm32cube/fdcan-node/main.c stm32cube/common/clock_160mhz.c)
    target_compile_definitions(hal-fdcan-rx-slow PRIVATE NODE_RX RX_READ_DELAY_MS=20)
    # TIM6 as the HAL time base: the unmodified template of the HAL driver.
    add_stm32cube_firmware(hal-tim stm32cube/tim/main.c stm32cube/common/clock_160mhz.c
        ${st_hal_SOURCE_DIR}/Src/stm32u5xx_hal_timebase_tim_template.c)

    # Official ST example projects, built unmodified.
    include(${CMAKE_CURRENT_LIST_DIR}/cmake/st-examples.cmake)
    add_st_example(st-spi-fullduplex-polling-master
        Projects/NUCLEO-U575ZI-Q/Examples/SPI/SPI_FullDuplex_ComPolling_Master
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_spi hal_spi_ex)
    # Master side of the two-board example: its readme selects it by defining MASTER_BOARD.
    add_st_example(st-i2c-twoboards-polling-master
        Projects/NUCLEO-U575ZI-Q/Examples/I2C/I2C_TwoBoards_ComPolling
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_i2c hal_i2c_ex)
    target_compile_definitions(st-i2c-twoboards-polling-master PRIVATE MASTER_BOARD)
    add_st_example(st-fdcan-loopback
        Projects/STM32U575I-EV/Examples/FDCAN/FDCAN_Loopback
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_fdcan)
    add_st_example(st-dma-flash-to-ram
        Projects/NUCLEO-U575ZI-Q/Examples/DMA/DMA_FLASHToRAM
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti)
endif()
