# Official STM32CubeU5 v1.9.0 example projects for NUCLEO-U575ZI-Q, built from unmodified ST
# sources: the example's own Src/Inc files, its STM32CubeIDE start-up code and linker script,
# the HAL and CMSIS components fetched by stm32cube.cmake, and the Nucleo BSP. Only the listed
# example directories are checked out (sparse, blobless clone). The sources are downloaded at
# configure time and are not part of this repository (BSD-3-Clause).
#
#   STM32CubeU5          v1.9.0   d88042df24f16799957c24f00c2b234c9e306188
#   stm32u5xx-nucleo-bsp v1.2.3   ed50f1d2d4940e4efe23104027b8008ce79037f5
#   stm32u575i-ev-bsp    v1.4.1   83d8fb77a80a4c6d458f37f6ec10471ba95ddc96
#   stm32-mfxstm32l152   v4.0.1   7f4389efee9c6a655b55e5df3fceef5586b35f9b
#   stm32-bsp-Common     v7.2.1   cf3581ba46e88944b604f6961505559b9efdac56
#   stm32-mw-threadx     v6.4.0_260327 b44bdbbbb5a9e4fb56728d2932236564c89db29a
#     (the Drivers/BSP/STM32U5xx_Nucleo, STM32U575I-EV, Components/mfxstm32l152,
#     Components/Common and Middlewares/ST/threadx submodule revisions of v1.9.0)

set(STM32CUBEU5_TAG v1.9.0)
set(STM32CUBEU5_COMMIT d88042df24f16799957c24f00c2b234c9e306188)
set(STM32CUBEU5_EXAMPLES
    Projects/NUCLEO-U575ZI-Q/Examples/SPI/SPI_FullDuplex_ComPolling_Master
    Projects/NUCLEO-U575ZI-Q/Examples/I2C/I2C_TwoBoards_ComPolling
    Projects/STM32U575I-EV/Examples/FDCAN/FDCAN_Loopback
    Projects/STM32U575I-EV/Examples/SD/SD_ReadWrite_DMALinkedList
    Projects/NUCLEO-U575ZI-Q/Examples/DMA/DMA_FLASHToRAM
    Projects/NUCLEO-U575ZI-Q/Examples/SPI/SPI_FullDuplex_ComDMA_Master
    Projects/NUCLEO-U575ZI-Q/Examples/RNG/RNG_MultiRNG
    Projects/NUCLEO-U575ZI-Q/Examples/RNG/RNG_MultiRNG_IT
    Projects/NUCLEO-U575ZI-Q/Examples/HASH/HASH_HMAC_SHA1MD5
    Projects/NUCLEO-U575ZI-Q/Templates/TrustZoneEnabled
    Projects/NUCLEO-U575ZI-Q/Examples/GTZC/GTZC_TZSC_MPCBB_TrustZone
    Projects/NUCLEO-U575ZI-Q/Examples/GPIO/GPIO_IOToggle_TrustZone
    Projects/NUCLEO-U575ZI-Q/Examples/RTC/RTC_TrustZone
    Projects/NUCLEO-U575ZI-Q/Examples/FLASH/FLASH_EraseProgram_TrustZone
    Projects/NUCLEO-U575ZI-Q/Applications/ThreadX/Tx_SecureLEDToggle_TrustZone)

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
# Evaluation board I/O expander (MFX) driver for the examples with USE_BSP_IO_CLASS = 1. The
# BSP includes "../Components/mfxstm32l152/mfxstm32l152.h" and the driver "../Common/io.h", so
# both components are placed in a Components directory as in STM32CubeU5.
set(st_bsp_components_dir ${CMAKE_BINARY_DIR}/_deps/st_bsp/Components)
fetch_st_component(st_mfxstm32l152 https://github.com/STMicroelectronics/stm32-mfxstm32l152.git
                   v4.0.1 7f4389efee9c6a655b55e5df3fceef5586b35f9b ${st_bsp_components_dir}/mfxstm32l152)
fetch_st_component(st_bsp_common https://github.com/STMicroelectronics/stm32-bsp-Common.git
                   v7.2.1 cf3581ba46e88944b604f6961505559b9efdac56 ${st_bsp_components_dir}/Common)

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

fetch_st_component(st_threadx https://github.com/STMicroelectronics/stm32-mw-threadx.git
                   v6.4.0_260327 b44bdbbbb5a9e4fb56728d2932236564c89db29a)

# add_st_threadx_trustzone_example(<target> <example path> <HAL modules...>): a TrustZone
# example with ThreadX in the non-secure image (Cortex-M33 GNU port with secure stacks), as in
# its STM32CubeIDE projects: the ThreadX common sources (tx_ and txe_), the port assembly and
# secure stack services in the non-secure image, tx_thread_secure_stack.c in the secure one.
function(add_st_threadx_trustzone_example name example)
    add_st_trustzone_example(${name} ${example} ${ARGN})
    set(dir ${st_examples_dir}/${example})
    set(port ${st_threadx_SOURCE_DIR}/ports/cortex_m33/gnu)
    # The ThreadX common sources listed in the non-secure STM32CubeIDE project.
    file(READ ${dir}/STM32CubeIDE/NonSecure/.project project)
    string(REGEX MATCHALL "threadx/common/src/[a-z0-9_]+[.]c" listed "${project}")
    list(REMOVE_DUPLICATES listed)
    set(threadx_common)
    foreach(file ${listed})
        string(REPLACE "threadx/" "${st_threadx_SOURCE_DIR}/" file ${file})
        list(APPEND threadx_common ${file})
    endforeach()
    foreach(file tx_thread_context_restore tx_thread_context_save tx_thread_interrupt_control
                 tx_thread_interrupt_disable tx_thread_interrupt_restore tx_thread_schedule
                 tx_thread_secure_stack_allocate tx_thread_secure_stack_free
                 tx_thread_secure_stack_initialize tx_thread_stack_build tx_thread_system_return
                 tx_timer_interrupt)
        list(APPEND threadx_port ${port}/src/${file}.S)
    endforeach()
    target_sources(${name}-nonsecure PRIVATE ${threadx_common} ${threadx_port}
        ${port}/src/txe_thread_secure_stack_allocate.c ${port}/src/txe_thread_secure_stack_free.c
        ${dir}/NonSecure/Src/tx_initialize_low_level.S)
    target_compile_definitions(${name}-nonsecure PRIVATE TX_INCLUDE_USER_DEFINE_FILE)
    target_sources(${name}-secure PRIVATE ${port}/src/tx_thread_secure_stack.c)
    foreach(world secure nonsecure)
        target_include_directories(${name}-${world} PRIVATE ${st_threadx_SOURCE_DIR}/common/inc ${port}/inc)
    endforeach()
endfunction()

# add_st_eval_io(<target>): the evaluation board I/O expander sources (BSP IO class).
function(add_st_eval_io name)
    target_sources(${name} PRIVATE ${st_eval_bsp_SOURCE_DIR}/stm32u575i_eval_bus.c
        ${st_eval_bsp_SOURCE_DIR}/stm32u575i_eval_io.c
        ${st_bsp_components_dir}/mfxstm32l152/mfxstm32l152.c
        ${st_bsp_components_dir}/mfxstm32l152/mfxstm32l152_reg.c)
    target_include_directories(${name} PRIVATE ${st_bsp_components_dir}
        ${st_bsp_components_dir}/mfxstm32l152)
endfunction()

# add_st_trustzone_example(<target> <example path> <HAL modules...>): a TrustZone project of two
# STM32CubeIDE projects, built as in their .cproject files. <target>-secure is built with CMSE
# ("Secure" project mode) and writes the import library of its non-secure callable functions
# (secure_nsclib.o); <target>-nonsecure links against it. A world with
# stm32u5xx_nucleo_conf.h uses the Nucleo BSP.
function(add_st_trustzone_example name example)
    set(dir ${st_examples_dir}/${example})
    set(hal_sources)
    foreach(module ${ARGN})
        list(APPEND hal_sources ${st_hal_SOURCE_DIR}/Src/stm32u5xx_${module}.c)
    endforeach()
    set(implib ${CMAKE_CURRENT_BINARY_DIR}/${name}-secure_nsclib.o)
    foreach(world Secure NonSecure)
        string(TOLOWER ${world} suffix)
        set(target ${name}-${suffix})
        file(GLOB world_sources ${dir}/${world}/Src/*.c)
        # The folders of these files in the STM32CubeIDE project differ between projects.
        file(GLOB_RECURSE startup ${dir}/STM32CubeIDE/${world}/*.s)
        file(GLOB_RECURSE user_sources ${dir}/STM32CubeIDE/${world}/syscalls.c
             ${dir}/STM32CubeIDE/${world}/sysmem.c)
        file(GLOB linker_script ${dir}/STM32CubeIDE/${world}/*_FLASH.ld)
        # The Nucleo BSP, for the worlds that configure it.
        set(bsp_sources)
        if(EXISTS ${dir}/${world}/Inc/stm32u5xx_nucleo_conf.h)
            set(bsp_sources ${st_nucleo_bsp_SOURCE_DIR}/stm32u5xx_nucleo.c)
        endif()
        add_executable(${target} ${world_sources} ${startup} ${hal_sources} ${user_sources}
            ${bsp_sources})
        set_target_properties(${target} PROPERTIES SUFFIX ".elf")
        target_compile_definitions(${target} PRIVATE STM32U575xx USE_HAL_DRIVER DEBUG)
        target_include_directories(${target} PRIVATE ${dir}/${world}/Inc ${dir}/Secure_nsclib
            ${st_nucleo_bsp_SOURCE_DIR} ${st_hal_SOURCE_DIR}/Inc ${st_hal_SOURCE_DIR}/Inc/Legacy
            ${st_device_SOURCE_DIR}/Include ${st_cmsis_core_SOURCE_DIR}/Include)
        target_compile_options(${target} PRIVATE ${STM32CUBE_FLAGS} -std=gnu11 -O0 -g3
            -ffunction-sections -fdata-sections)
        target_link_options(${target} PRIVATE ${STM32CUBE_FLAGS} --specs=nano.specs -T${linker_script}
            -Wl,--gc-sections -Wl,-Map=${target}.map)
    endforeach()
    target_compile_options(${name}-secure PRIVATE -mcmse)
    target_link_options(${name}-secure PRIVATE -Wl,--cmse-implib,--out-implib=${implib})
    # The linker writes the import library; declaring it as a by-product orders the
    # non-secure link after it.
    add_custom_command(TARGET ${name}-secure POST_BUILD COMMAND ${CMAKE_COMMAND} -E true
                       BYPRODUCTS ${implib})
    add_dependencies(${name}-nonsecure ${name}-secure)
    target_link_libraries(${name}-nonsecure PRIVATE ${implib})
endfunction()
