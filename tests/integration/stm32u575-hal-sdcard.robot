*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) driving an SD card in SPI mode
...                 (STM32U5_SDCard, adapted from Renode SD.SDCard; 4 GB SDHC, image file) on SPI1 with a software chip select
...                 on PD2 (firmware-tests/stm32cube/sdcard): CMD0/CMD8/ACMD41/CMD58 initialization,
...                 CMD9 CSD, CMD24 writes and CMD17 reads of two sectors, one HAL call per byte, and
...                 a command interrupted by releasing CS.
...                 The chip select pin is connected to the SPI chip select input (GPIO 0): with
...                 software NSS the card transaction lasts while CS is low.
Library             OperatingSystem
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL SD Card Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-sdcard.elf
${STEP_DONE}        99
${FIRST_SECTOR}     100
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL SD Card Firmware
    Create STM32U575 Machine
    Create Log Tester               0
    # The image file is empty: the card reads zeros beyond its end and grows it on writes.
    ${directory}=                   Evaluate           $TEMPDIR.replace(chr(92), '/')
    ${image}=                       Set Variable       ${directory}/stm32u575-sdcard.img
    Create Binary File              ${image}           ${EMPTY}
    ${card}=                        Catenate    SEPARATOR=\n
    ...                             sdCard: SD.STM32U5_SDCard @ spi1
    ...                             ${SPACE * 4}imageFile: "${image}"
    ...                             ${SPACE * 4}capacity: 0x100000000
    ...                             ${SPACE * 4}spiMode: true
    ...                             ${SPACE * 4}persistent: true
    ...                             ${EMPTY}
    ...                             gpioPortD:
    ...                             ${SPACE * 4}2 -> spi1@0
    ${fixture}=                     Set Variable       ${directory}/stm32u575-sdcard.repl
    Create File                     ${fixture}         ${card}
    Execute Command                 machine LoadPlatformDescription @${fixture}
    Set Test Variable               ${IMAGE}           ${image}
    Execute Command                 sysbus LoadELF @${ELF}
    Execute Command                 emulation RunFor "0.5"

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    Word Should Be                  ${address}         ${expected}

*** Test Cases ***
Should Initialize The Card And Write And Read Two Sectors
    Symbol Should Be                failedStep         0
    Symbol Should Be                step               ${STEP_DONE}
    Symbol Should Be                highCapacity       1
    Symbol Should Be                sectorsOk          2
    # Two bytes of a command, then CS high: the next command is answered (FinishTransmission).
    Symbol Should Be                interruptedCommandOk    1
    # CSD version 2 of a 4 GB card: C_SIZE + 1 = 8192 units of 512 KB, 8388608 sectors.
    Symbol Should Be                sectorCount        ${{8192 * 1024}}
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true

Should Store The Written Sectors In The Image File
    # Sector 100 holds index * 7 + 1 for byte index, sector 101 index * 7 + 0x56.
    ${data}=                        Get Binary File    ${IMAGE}
    ${offset}=                      Evaluate           ${FIRST_SECTOR} * 512
    ${expected}=                    Evaluate           bytes((index * 7 + 1) & 0xFF for index in range(512)) + bytes((index * 7 + 0x56) & 0xFF for index in range(512))
    ${actual}=                      Evaluate           $data[${offset}:${offset} + 1024]
    Should Be Equal                 ${actual}          ${expected}
