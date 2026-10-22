# Official STM32CubeU5 v1.9.0 example projects for NUCLEO-U575ZI-Q, built from unmodified ST
# sources: the example's own Src/Inc files, its STM32CubeIDE start-up code and linker script,
# the HAL and CMSIS components fetched by stm32cube.cmake, and the Nucleo BSP. Only the listed
# example directories are checked out (sparse, blobless clone). The sources are downloaded at
# configure time and are not part of this repository (BSD-3-Clause).
#
#   STM32CubeU5          v1.9.0   d88042df24f16799957c24f00c2b234c9e306188
#   stm32u5xx-nucleo-bsp v1.2.3   ed50f1d2d4940e4efe23104027b8008ce79037f5
#   stm32u575i-ev-bsp    v1.4.1   83d8fb77a80a4c6d458f37f6ec10471ba95ddc96
#     (the Drivers/BSP/STM32U5xx_Nucleo and STM32U575I-EV submodule revisions of v1.9.0)

set(STM32CUBEU5_TAG v1.9.0)
set(STM32CUBEU5_COMMIT d88042df24f16799957c24f00c2b234c9e306188)
set(STM32CUBEU5_EXAMPLES
    Projects/NUCLEO-U575ZI-Q/Examples/SPI/SPI_FullDuplex_ComPolling_Master
    Projects/NUCLEO-U575ZI-Q/Examples/I2C/I2C_TwoBoards_ComPolling
    Projects/STM32U575I-EV/Examples/FDCAN/FDCAN_Loopback
    Projects/NUCLEO-U575ZI-Q/Examples/DMA/DMA_FLASHToRAM
    Projects/NUCLEO-U575ZI-Q/Examples/SPI/SPI_FullDuplex_ComDMA_Master)

set(st_examples_dir ${CMAKE_BINARY_DIR}/_deps/stm32cubeu5-examples)
if(NOT EXISTS ${st_examples_dir}/.git)
    execute_process(COMMAND git clone -q --filter=blob:none --no-checkout --depth 1
                            --branch ${STM32CUBEU5_TAG}
                            https://github.com/STMicroelectronics/STM32CubeU5.git ${st_examples_dir}
                    RESULT_VARIABLE result)
    if(result)
        message(FATAL_ERROR "STM32CubeU5: clone failed")
    endif()
endif()
set(sparse_paths)
foreach(example ${STM32CUBEU5_EXAMPLES})
    list(APPEND sparse_paths /${example}/)
endforeach()
execute_process(COMMAND git -C ${st_examples_dir} sparse-checkout set --no-cone ${sparse_paths}
                RESULT_VARIABLE result)
if(NOT result)
    execute_process(COMMAND git -C ${st_examples_dir} checkout -q RESULT_VARIABLE result)
endif()
if(result)
    message(FATAL_ERROR "STM32CubeU5: sparse checkout failed")
endif()
execute_process(COMMAND git -C ${st_examples_dir} rev-parse HEAD
                OUTPUT_VARIABLE fetched OUTPUT_STRIP_TRAILING_WHITESPACE)
if(NOT fetched STREQUAL STM32CUBEU5_COMMIT)
    message(FATAL_ERROR "STM32CubeU5: tag ${STM32CUBEU5_TAG} is ${fetched}, expected ${STM32CUBEU5_COMMIT}")
endif()

fetch_st_component(st_nucleo_bsp https://github.com/STMicroelectronics/stm32u5xx-nucleo-bsp.git
                   v1.2.3 ed50f1d2d4940e4efe23104027b8008ce79037f5)
fetch_st_component(st_eval_bsp https://github.com/STMicroelectronics/stm32u575i-ev-bsp.git
                   v1.4.1 83d8fb77a80a4c6d458f37f6ec10471ba95ddc96)

# add_st_example(<target> <example path> <HAL modules...>): the HAL is compiled per example
# because each example has its own stm32u5xx_hal_conf.h. Debug options as in the STM32CubeIDE
# project (DEBUG defined, -O0).
function(add_st_example name example)
    set(dir ${st_examples_dir}/${example})
    # Board support package of the example's board (Projects/<board>/...).
    if(example MATCHES "^Projects/STM32U575I-EV/")
        set(bsp_dir ${st_eval_bsp_SOURCE_DIR})
        set(bsp_sources ${st_eval_bsp_SOURCE_DIR}/stm32u575i_eval.c)
    else()
        set(bsp_dir ${st_nucleo_bsp_SOURCE_DIR})
        set(bsp_sources ${st_nucleo_bsp_SOURCE_DIR}/stm32u5xx_nucleo.c)
    endif()
    file(GLOB example_sources ${dir}/Src/*.c)
    file(GLOB startup ${dir}/STM32CubeIDE/Application/Startup/*.s)
    file(GLOB linker_script ${dir}/STM32CubeIDE/*_FLASH.ld)
    set(hal_sources)
    foreach(module ${ARGN})
        list(APPEND hal_sources ${st_hal_SOURCE_DIR}/Src/stm32u5xx_${module}.c)
    endforeach()
    add_executable(${name} ${example_sources} ${startup} ${hal_sources}
        ${dir}/STM32CubeIDE/Application/User/syscalls.c
        ${dir}/STM32CubeIDE/Application/User/sysmem.c
        ${bsp_sources})
    set_target_properties(${name} PROPERTIES SUFFIX ".elf")
    target_compile_definitions(${name} PRIVATE STM32U575xx USE_HAL_DRIVER DEBUG)
    target_include_directories(${name} PRIVATE ${dir}/Inc ${bsp_dir}
        ${st_hal_SOURCE_DIR}/Inc ${st_hal_SOURCE_DIR}/Inc/Legacy
        ${st_device_SOURCE_DIR}/Include ${st_cmsis_core_SOURCE_DIR}/Include)
    target_compile_options(${name} PRIVATE ${STM32CUBE_FLAGS} -std=gnu11 -O0 -g3
        -ffunction-sections -fdata-sections)
    target_link_options(${name} PRIVATE ${STM32CUBE_FLAGS} --specs=nano.specs -T${linker_script}
        -Wl,--gc-sections -Wl,-Map=${name}.map)
endfunction()
