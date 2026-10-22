# Fetches the STMicroelectronics components of STM32CubeU5 v1.9.0 at pinned commits and builds
# a static HAL library. The sources are downloaded at configure time and are not part of this
# repository (HAL and device files: BSD-3-Clause; CMSIS Core: Apache-2.0).
#
# Component revisions are those referenced by STM32CubeU5 v1.9.0:
#   stm32u5xx-hal-driver v1.6.3   2552682ee5a0d6f826a6435750fc9267be65868b
#   cmsis-device-u5      v1.4.3   624374fa1e21ca195d6f2102ac0caaa50d0ea4c8
#   cmsis-core           v5.9.0_20250520 b7487de1303e2eb77c402432e368691e9dcfb5f0
#     (Include/ files identical to STM32CubeU5 v1.9.0 Drivers/CMSIS/Core/Include)

include(FetchContent)

function(fetch_st_component name repository tag commit)
    FetchContent_Declare(${name}
        GIT_REPOSITORY ${repository}
        GIT_TAG ${tag}
        GIT_SHALLOW TRUE
        # The repositories have no CMake project; only download them.
        SOURCE_SUBDIR do-not-add-subdirectory)
    FetchContent_MakeAvailable(${name})
    execute_process(COMMAND git -C ${${name}_SOURCE_DIR} rev-parse HEAD
                    OUTPUT_VARIABLE fetched OUTPUT_STRIP_TRAILING_WHITESPACE)
    if(NOT fetched STREQUAL commit)
        message(FATAL_ERROR "${name}: tag ${tag} is ${fetched}, expected ${commit}")
    endif()
    set(${name}_SOURCE_DIR ${${name}_SOURCE_DIR} PARENT_SCOPE)
endfunction()

fetch_st_component(st_hal https://github.com/STMicroelectronics/stm32u5xx-hal-driver.git
                   v1.6.3 2552682ee5a0d6f826a6435750fc9267be65868b)
fetch_st_component(st_device https://github.com/STMicroelectronics/cmsis-device-u5.git
                   v1.4.3 624374fa1e21ca195d6f2102ac0caaa50d0ea4c8)
fetch_st_component(st_cmsis_core https://github.com/STMicroelectronics/cmsis-core.git
                   v5.9.0_20250520 b7487de1303e2eb77c402432e368691e9dcfb5f0)

set(STM32CUBE_FLAGS -mcpu=cortex-m33 -mthumb -mfpu=fpv5-sp-d16 -mfloat-abi=hard)
set(STM32CUBE_DEFINES STM32U575xx USE_HAL_DRIVER)
set(STM32CUBE_INCLUDES
    ${CMAKE_CURRENT_LIST_DIR}/../stm32cube/common
    ${st_hal_SOURCE_DIR}/Inc
    ${st_device_SOURCE_DIR}/Include
    ${st_cmsis_core_SOURCE_DIR}/Include)

set(hal_modules hal hal_cortex hal_rcc hal_rcc_ex hal_pwr hal_pwr_ex hal_flash hal_flash_ex
    hal_icache hal_gpio hal_dma hal_dma_ex hal_exti hal_uart hal_uart_ex hal_spi hal_spi_ex)
set(hal_sources)
foreach(module ${hal_modules})
    list(APPEND hal_sources ${st_hal_SOURCE_DIR}/Src/stm32u5xx_${module}.c)
endforeach()

add_library(stm32u5_hal STATIC ${hal_sources})
target_compile_definitions(stm32u5_hal PUBLIC ${STM32CUBE_DEFINES})
target_include_directories(stm32u5_hal PUBLIC ${STM32CUBE_INCLUDES})
target_compile_options(stm32u5_hal PUBLIC ${STM32CUBE_FLAGS} -O1 -g -ffunction-sections -fdata-sections)

# Firmware built with the unmodified ST start-up code, system file and linker script.
function(add_stm32cube_firmware name)
    add_executable(${name} ${ARGN}
        ${st_device_SOURCE_DIR}/Source/Templates/system_stm32u5xx.c
        ${st_device_SOURCE_DIR}/Source/Templates/gcc/startup_stm32u575xx.s)
    set_target_properties(${name} PROPERTIES SUFFIX ".elf")
    target_link_libraries(${name} PRIVATE stm32u5_hal)
    target_compile_options(${name} PRIVATE -std=c11 -Wall -Wextra)
    target_link_options(${name} PRIVATE ${STM32CUBE_FLAGS} --specs=nano.specs --specs=nosys.specs
        -T${st_device_SOURCE_DIR}/Source/Templates/gcc/linker/STM32U575xx_FLASH.ld
        -Wl,--gc-sections -Wl,-Map=${name}.map)
endfunction()
