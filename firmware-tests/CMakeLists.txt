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

# TrustZone (TZEN = 1): a secure image built with CMSE that exports its non-secure callable
# veneers as an import library, and a non-secure image linked against it.
set(TRUSTZONE_DIR ${CMAKE_CURRENT_LIST_DIR}/baremetal/trustzone)
set(TRUSTZONE_IMPLIB ${CMAKE_CURRENT_BINARY_DIR}/trustzone-secure-implib.o)
function(add_trustzone_image name linker_script)
    add_executable(${name} ${ARGN} ${CMAKE_CURRENT_LIST_DIR}/baremetal/common/startup.c)
    set_target_properties(${name} PROPERTIES SUFFIX ".elf")
    target_include_directories(${name} PRIVATE ${CMAKE_CURRENT_LIST_DIR}/baremetal/common
                               ${TRUSTZONE_DIR})
    target_compile_options(${name} PRIVATE ${CORTEX_M33_FLAGS} -std=c11 -O1 -g -Wall -Wextra -Werror
                           -ffunction-sections -fdata-sections)
    target_link_options(${name} PRIVATE ${CORTEX_M33_FLAGS} -nostartfiles -nostdlib
                        -T${TRUSTZONE_DIR}/${linker_script}
                        -Wl,--gc-sections -Wl,-Map=${name}.map)
endfunction()
add_trustzone_image(trustzone-secure secure.ld ${TRUSTZONE_DIR}/secure.c)
target_compile_options(trustzone-secure PRIVATE -mcmse)
# The non-secure callable functions are called only from the other image: keep them. Calls
# to non-secure code use __gnu_cmse_nonsecure_call from libgcc.
target_link_options(trustzone-secure PRIVATE -Wl,--no-gc-sections
                    -Wl,--cmse-implib,--out-implib=${TRUSTZONE_IMPLIB})
target_link_libraries(trustzone-secure PRIVATE gcc)
# The linker writes the import library; declaring it as a by-product lets the build order the
# non-secure link after it.
add_custom_command(TARGET trustzone-secure POST_BUILD COMMAND ${CMAKE_COMMAND} -E true
                   BYPRODUCTS ${TRUSTZONE_IMPLIB})
add_trustzone_image(trustzone-nonsecure nonsecure.ld ${TRUSTZONE_DIR}/nonsecure.c)
add_dependencies(trustzone-nonsecure trustzone-secure)
target_link_libraries(trustzone-nonsecure PRIVATE ${TRUSTZONE_IMPLIB})

# STM32CubeU5 HAL firmware. Requires network access at configure time.
option(STM32CUBE_FIRMWARE "Build firmware based on the STM32CubeU5 HAL" ON)
if(STM32CUBE_FIRMWARE)
    include(${CMAKE_CURRENT_LIST_DIR}/cmake/stm32cube.cmake)
    add_stm32cube_firmware(hal-clock stm32cube/clock/main.c)
    add_stm32cube_firmware(hal-gpio stm32cube/gpio/main.c)
    add_stm32cube_firmware(hal-uart stm32cube/uart/main.c)
    add_stm32cube_firmware(hal-spi stm32cube/spi/main.c stm32cube/common/clock_160mhz.c)
    add_stm32cube_firmware(hal-sdcard stm32cube/sdcard/main.c stm32cube/common/clock_160mhz.c)
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
    add_st_example(st-rng-multirng
        Projects/NUCLEO-U575ZI-Q/Examples/RNG/RNG_MultiRNG
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_rng hal_rng_ex)
    add_st_example(st-rng-multirng-it
        Projects/NUCLEO-U575ZI-Q/Examples/RNG/RNG_MultiRNG_IT
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_rng hal_rng_ex)
    # 24 MB written and read back through a circular two-node IDMA linked list of 1 KB buffers.
    add_st_example(st-sd-readwrite-dma-linkedlist
        Projects/STM32U575I-EV/Examples/SD/SD_ReadWrite_DMALinkedList
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_i2c hal_i2c_ex hal_sd hal_sd_ex ll_sdmmc)
    add_st_eval_io(st-sd-readwrite-dma-linkedlist)
    # HMAC-SHA1 and HMAC-MD5 of a text, compared with the expected digests.
    add_st_example(st-hash-hmac-sha1md5
        Projects/NUCLEO-U575ZI-Q/Examples/HASH/HASH_HMAC_SHA1MD5
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_hash hal_hash_ex)
    add_st_example(st-dma-flash-to-ram
        Projects/NUCLEO-U575ZI-Q/Examples/DMA/DMA_FLASHToRAM
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti)
    # TrustZone template (TZEN = 1): secure isolation setup, non-secure LED blink.
    add_st_trustzone_example(st-trustzone-template
        Projects/NUCLEO-U575ZI-Q/Templates/TrustZoneEnabled
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc)
    # MPCBB illegal access: the non-secure image reads secure SRAM1 on a button press.
    add_st_trustzone_example(st-gtzc-tzsc-mpcbb
        Projects/NUCLEO-U575ZI-Q/Examples/GTZC/GTZC_TZSC_MPCBB_TrustZone
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc)
    # Secure SysTick toggles the secure LED1, non-secure SysTick the non-secure LED2.
    add_st_trustzone_example(st-gpio-iotoggle-trustzone
        Projects/NUCLEO-U575ZI-Q/Examples/GPIO/GPIO_IOToggle_TrustZone
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc)
    # RTC with secure and non-secure features, TAMP backup register zones.
    add_st_trustzone_example(st-rtc-trustzone
        Projects/NUCLEO-U575ZI-Q/Examples/RTC/RTC_TrustZone
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc hal_rtc hal_rtc_ex)
    # ThreadX in the non-secure world toggles LED1 and calls a secure function that toggles LED2.
    add_st_threadx_trustzone_example(st-tx-secure-led-toggle
        Projects/NUCLEO-U575ZI-Q/Applications/ThreadX/Tx_SecureLEDToggle_TrustZone
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc hal_tim hal_tim_ex)
    # Secure and non-secure flash erase and programming.
    add_st_trustzone_example(st-flash-erase-program-trustzone
        Projects/NUCLEO-U575ZI-Q/Examples/FLASH/FLASH_EraseProgram_TrustZone
        hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex hal_icache
        hal_gpio hal_dma hal_dma_ex hal_exti hal_gtzc)
endif()
