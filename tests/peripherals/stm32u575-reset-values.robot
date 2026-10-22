*** Settings ***
Documentation       Reset values of STM32U575 registers (RCC, PWR, FLASH, ICACHE, SPI, I2C, FDCAN, GPDMA)
...                 against RM0456 Rev 7. Data: stm32u575_reset_values.py, generated from the CMSIS
...                 header and the manual text.
Library             String
Library             Collections
Variables           ${CURDIR}/stm32u575_reset_values.py
Test Setup          Create STM32U575 Machine

*** Variables ***
${ROOT}             ${CURDIR}/../..

# Documented differences between the model and RM0456 (see the model headers).
&{DEVIATIONS}       ICACHE_SR=0x00000002    FDCAN1_PSR=0x0000070F

*** Keywords ***
Create STM32U575 Machine
    Execute Command                 include @${ROOT}/scripts/load-models.resc
    Execute Command                 mach create "stm32u575"
    Execute Command                 machine LoadPlatformDescription @${ROOT}/platforms/cpus/stm32u575.repl

Register Should Match Reset Value
    [Arguments]                     ${name}    ${address}    ${expected}    ${mask}    ${page}    ${source}
    ${expected}=                    Get From Dictionary    ${DEVIATIONS}    ${name}    default=${expected}
    ${address}=                     Convert To Hex     ${address}    prefix=0x    length=8
    ${value}=                       Execute Command    sysbus ReadDoubleWord ${address}
    ${value}=                       Strip String       ${value}
    ${masked}=                      Evaluate           int("${value}", 16) & ${mask}
    ${wanted}=                      Evaluate           ${expected} & ${mask}
    Should Be Equal As Integers     ${masked}          ${wanted}
    ...    msg=${name} at ${address}: read ${value}, RM0456 p.${page} gives ${source}    values=False

*** Test Cases ***
Should Have Documented Reset Values
    [Documentation]                 One check per register; the failure message names the register and page.
    FOR    ${row}    IN    @{RESET_VALUES}
        Run Keyword And Continue On Failure    Register Should Match Reset Value    @{row}
    END
