*** Settings ***
Documentation       STM32U575 flash interface (RM0456 Rev 7 section 7): latency, locking and the
...                 effect of erase operations on the flash memory contents.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine

*** Variables ***
${FLASH_ACR}        0x40022000
${FLASH_NSKEYR}     0x40022008
${FLASH_OPTKEYR}    0x40022010
${FLASH_NSSR}       0x40022020
${FLASH_NSCR}       0x40022028

${BANK1}            0x08000000
${BANK2}            0x08100000
${PAGE_SIZE}        0x2000
${ERASED}           0xFFFFFFFF
${PATTERN}          0x5AA5C33C

${NSCR_PER}         0x00000002
${NSCR_MER1}        0x00000004
${NSCR_BKER}        0x00000800
${NSCR_MER2}        0x00008000
${NSCR_STRT}        0x00010000
${NSCR_EOPIE}       0x01000000
${NSCR_ERRIE}       0x02000000
${NSCR_OPTLOCK}     0x40000000
${NSCR_LOCK}        0x80000000
${NSSR_EOP}         0x00000001
${NSSR_OPERR}       0x00000002
${NSSR_PGSERR}      0x00000080

*** Keywords ***
Unlock Flash
    Write Word                      ${FLASH_NSKEYR}    0x45670123
    Write Word                      ${FLASH_NSKEYR}    0xCDEF89AB
    Bits Should Be Clear            ${FLASH_NSCR}      ${NSCR_LOCK}

Unlock Flash Should Fail
    [Documentation]                 A wrong key locks FLASH_NSCR until the next system reset (RM0456 section 7.3.5).
    Write Word                      ${FLASH_NSKEYR}    0x45670123
    Write Word                      ${FLASH_NSKEYR}    0xCDEF89AB
    Bits Should Be Set              ${FLASH_NSCR}      ${NSCR_LOCK}

Erase Page
    [Arguments]                     ${page}    ${bank_select}=0
    ${control}=                     Evaluate           ${NSCR_PER} | (${page} << 3) | ${bank_select} | ${NSCR_STRT}
    Write Word                      ${FLASH_NSCR}      ${control}

Fill Words
    [Arguments]                     @{addresses}
    FOR    ${address}    IN    @{addresses}
        Write Word                  ${address}         ${PATTERN}
    END

Words Should Be
    [Arguments]                     ${expected}    @{addresses}
    FOR    ${address}    IN    @{addresses}
        Word Should Be              ${address}         ${expected}
    END

*** Test Cases ***
Should Keep The Programmed Latency
    FOR    ${latency}    IN RANGE    16
        Write Word                  ${FLASH_ACR}       ${latency}
        Field Should Be             ${FLASH_ACR}       0    4    ${latency}
    END

Should Read Erased Flash As All Ones
    Words Should Be                 ${ERASED}          ${BANK1}    ${BANK2}    0x081FFFFC

Should Start Locked And Ignore Control Writes
    Word Should Be                  ${FLASH_NSCR}      ${NSCR_LOCK} | ${NSCR_OPTLOCK}
    Write Word                      ${FLASH_NSCR}      ${NSCR_PER}
    Word Should Be                  ${FLASH_NSCR}      ${NSCR_LOCK} | ${NSCR_OPTLOCK}

Should Unlock With The Key Sequence And Lock Again
    Unlock Flash
    Word Should Be                  ${FLASH_NSCR}      ${NSCR_OPTLOCK}
    Write Word                      ${FLASH_NSCR}      ${NSCR_LOCK}
    Bits Should Be Set              ${FLASH_NSCR}      ${NSCR_LOCK}

Should Stay Locked Until Reset After A Wrong Key
    Write Word                      ${FLASH_NSKEYR}    0x12345678
    Unlock Flash Should Fail
    Execute Command                 machine Reset
    Unlock Flash

Should Unlock Option Bytes Only After The Control Register
    Write Word                      ${FLASH_OPTKEYR}    0x08192A3B
    Write Word                      ${FLASH_OPTKEYR}    0x4C5D6E7F
    Bits Should Be Set              ${FLASH_NSCR}      ${NSCR_OPTLOCK}
    Unlock Flash
    Write Word                      ${FLASH_OPTKEYR}    0x08192A3B
    Write Word                      ${FLASH_OPTKEYR}    0x4C5D6E7F
    Bits Should Be Clear            ${FLASH_NSCR}      ${NSCR_OPTLOCK}

Should Erase Exactly One Page In Bank 1
    ${page2}=                       Evaluate           ${BANK1} + 2 * ${PAGE_SIZE}
    ${before}=                      Evaluate           ${page2} - 4
    ${last}=                        Evaluate           ${page2} + ${PAGE_SIZE} - 4
    ${after}=                       Evaluate           ${page2} + ${PAGE_SIZE}
    Fill Words                      ${before}    ${page2}    ${last}    ${after}
    Unlock Flash
    Erase Page                      2
    Words Should Be                 ${ERASED}    ${page2}    ${last}
    Words Should Be                 ${PATTERN}    ${before}    ${after}

Should Erase A Page Of Bank 2 Selected By BKER
    ${bank1_page1}=                 Evaluate           ${BANK1} + ${PAGE_SIZE}
    ${bank2_page1}=                 Evaluate           ${BANK2} + ${PAGE_SIZE}
    Fill Words                      ${bank1_page1}    ${bank2_page1}
    Unlock Flash
    Erase Page                      1    ${NSCR_BKER}
    Word Should Be                  ${bank2_page1}     ${ERASED}
    Word Should Be                  ${bank1_page1}     ${PATTERN}

Should Mass Erase One Bank Only
    Fill Words                      ${BANK1}    0x080FFFFC    ${BANK2}    0x081FFFFC
    Unlock Flash
    ${control}=                     Evaluate           ${NSCR_MER1} | ${NSCR_STRT}
    Write Word                      ${FLASH_NSCR}      ${control}
    Words Should Be                 ${ERASED}    ${BANK1}    0x080FFFFC
    Words Should Be                 ${PATTERN}    ${BANK2}    0x081FFFFC
    ${control}=                     Evaluate           ${NSCR_MER2} | ${NSCR_STRT}
    Write Word                      ${FLASH_NSCR}      ${control}
    Words Should Be                 ${ERASED}    ${BANK2}    0x081FFFFC

Should Report A Sequence Error For Start Without An Operation
    Unlock Flash
    Write Word                      ${FLASH_NSCR}      ${NSCR_STRT}
    Bits Should Be Set              ${FLASH_NSSR}      ${NSSR_PGSERR}
    Bits Should Be Clear            ${FLASH_NSSR}      ${NSSR_OPERR}
    Interrupt Line Should Be        flashController    False
    Write Word                      ${FLASH_NSSR}      ${NSSR_PGSERR}
    ${control}=                     Evaluate           ${NSCR_ERRIE} | ${NSCR_STRT}
    Write Word                      ${FLASH_NSCR}      ${control}
    Bits Should Be Set              ${FLASH_NSSR}      ${NSSR_PGSERR} | ${NSSR_OPERR}
    Interrupt Line Should Be        flashController    True
    Write Word                      ${FLASH_NSSR}      ${NSSR_PGSERR} | ${NSSR_OPERR}
    Word Should Be                  ${FLASH_NSSR}      0
    Interrupt Line Should Be        flashController    False

Should Report End Of Operation Only When Enabled
    Unlock Flash
    Erase Page                      0
    Bits Should Be Clear            ${FLASH_NSSR}      ${NSSR_EOP}
    ${control}=                     Evaluate           ${NSCR_EOPIE} | ${NSCR_PER} | ${NSCR_STRT}
    Write Word                      ${FLASH_NSCR}      ${control}
    Bits Should Be Set              ${FLASH_NSSR}      ${NSSR_EOP}
    Interrupt Line Should Be        flashController    True
    Write Word                      ${FLASH_NSSR}      ${NSSR_EOP}
    Bits Should Be Clear            ${FLASH_NSSR}      ${NSSR_EOP}
    Interrupt Line Should Be        flashController    False
