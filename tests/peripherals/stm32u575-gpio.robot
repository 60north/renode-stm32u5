*** Settings ***
Documentation       STM32U575 GPIO ports at register level (RM0456 Rev 7 section 13) with an LED on
...                 PA5 and buttons on PC13 and PB13 (tests/common/stm32u575-gpio-fixture.repl).
...                 The CPU is halted; inputs are applied by advancing virtual time.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create GPIO Fixture

*** Variables ***
${GPIOA}            0x42020000
${GPIOB}            0x42020400
${GPIOC}            0x42020800
${GPIOI}            0x42022000
${MODER}            0x00
${OTYPER}           0x04
${OSPEEDR}          0x08
${PUPDR}            0x0C
${IDR}              0x10
${ODR}              0x14
${BSRR}             0x18
${LCKR}             0x1C
${AFRL}             0x20
${AFRH}             0x24
${BRR}              0x28
${HSLVR}            0x2C
${SECCFGR}          0x30
${PIN5}             0x00000020
${PIN13}            0x00002000

*** Keywords ***
Create GPIO Fixture
    Create STM32U575 Machine        ${CURDIR}/../common/stm32u575-gpio-fixture.repl
    Execute Command                 cpu IsHalted true

Port Register
    [Arguments]                     ${port}    ${offset}
    ${address}=                     Evaluate           ${port} + ${offset}
    RETURN                          ${address}

Write Port
    [Arguments]                     ${port}    ${offset}    ${value}
    ${address}=                     Port Register      ${port}    ${offset}
    Write Word                      ${address}         ${value}

Port Should Be
    [Arguments]                     ${port}    ${offset}    ${expected}
    ${address}=                     Port Register      ${port}    ${offset}
    Word Should Be                  ${address}         ${expected}

LED Should Be
    [Arguments]                     ${expected}
    ${state}=                       Execute Command    sysbus.gpioPortA.led State
    ${state}=                       Strip String       ${state}
    Should Be Equal                 ${state}           ${expected}

Set Button
    [Arguments]                     ${button}    ${action}
    Execute Command                 ${button} ${action}
    Execute Command                 emulation RunFor "0.0001"

Configure PA5 As Output
    # MODER5 = 01 (general-purpose output), other pins keep their reset modes.
    Write Port                      ${GPIOA}    ${MODER}    0xABFFF7FF

Configure As Input
    [Arguments]                     ${port}    ${reset_mode}    ${pin}
    ${value}=                       Evaluate           ${reset_mode} & ~(3 << (2 * ${pin}))
    Write Port                      ${port}    ${MODER}    ${value}

*** Test Cases ***
Should Have Documented Reset Values
    [Documentation]                 RM0456 sections 13.4.1-13.4.13 (STM32U575/585 values).
    Port Should Be                  ${GPIOA}    ${MODER}      0xABFFFFFF
    Port Should Be                  ${GPIOA}    ${OSPEEDR}    0x0C000000
    Port Should Be                  ${GPIOA}    ${PUPDR}      0x64000000
    Port Should Be                  ${GPIOB}    ${MODER}      0xFFFFFEBF
    Port Should Be                  ${GPIOB}    ${OSPEEDR}    0x000000C0
    Port Should Be                  ${GPIOB}    ${PUPDR}      0x00000100
    Port Should Be                  ${GPIOC}    ${MODER}      0xFFFFFFFF
    Port Should Be                  ${GPIOI}    ${MODER}      0x0000FFFF
    FOR    ${port}    IN    ${GPIOA}    ${GPIOB}    ${GPIOC}    ${GPIOI}
        Port Should Be              ${port}    ${OTYPER}     0
        Port Should Be              ${port}    ${ODR}        0
        Port Should Be              ${port}    ${AFRL}       0
        Port Should Be              ${port}    ${AFRH}       0
        Port Should Be              ${port}    ${HSLVR}      0
        Port Should Be              ${port}    ${SECCFGR}    0x0000FFFF
    END

Should Drive The Output From ODR
    Configure PA5 As Output
    LED Should Be                   False
    Write Port                      ${GPIOA}    ${ODR}    ${PIN5}
    LED Should Be                   True
    Port Should Be                  ${GPIOA}    ${ODR}    ${PIN5}
    Write Port                      ${GPIOA}    ${ODR}    0
    LED Should Be                   False

Should Set And Reset Atomically
    Configure PA5 As Output
    Write Port                      ${GPIOA}    ${BSRR}    ${PIN5}
    LED Should Be                   True
    Write Port                      ${GPIOA}    ${BSRR}    0x00200000
    LED Should Be                   False
    Write Port                      ${GPIOA}    ${BSRR}    ${PIN5}
    Write Port                      ${GPIOA}    ${BRR}     ${PIN5}
    LED Should Be                   False
    # Set has priority when BS and BR of the same pin are written together (RM0456 13.4.7).
    Write Port                      ${GPIOA}    ${BSRR}    0x00200020
    LED Should Be                   True

Should Report External Input In IDR
    Configure As Input              ${GPIOC}    0xFFFFFFFF    13
    Port Should Be                  ${GPIOC}    ${IDR}    0
    Set Button                      sysbus.gpioPortC.buttonC13    Press
    Port Should Be                  ${GPIOC}    ${IDR}    ${PIN13}
    Set Button                      sysbus.gpioPortC.buttonC13    Release
    Port Should Be                  ${GPIOC}    ${IDR}    0

Should Read Zero From A Pin In Analog Mode
    [Documentation]                 PC13 stays in its reset (analog) mode.
    Set Button                      sysbus.gpioPortC.buttonC13    Press
    Port Should Be                  ${GPIOC}    ${IDR}    0

Should Keep The Pin Configuration
    Write Port                      ${GPIOB}    ${OTYPER}     0x00000300
    Write Port                      ${GPIOB}    ${OSPEEDR}    0x000F00C0
    Write Port                      ${GPIOB}    ${PUPDR}      0x00000140
    Write Port                      ${GPIOB}    ${AFRL}       0x77000000
    Write Port                      ${GPIOB}    ${AFRH}       0x000000AB
    Port Should Be                  ${GPIOB}    ${OTYPER}     0x00000300
    Port Should Be                  ${GPIOB}    ${OSPEEDR}    0x000F00C0
    Port Should Be                  ${GPIOB}    ${PUPDR}      0x00000140
    Port Should Be                  ${GPIOB}    ${AFRL}       0x77000000
    Port Should Be                  ${GPIOB}    ${AFRH}       0x000000AB
    Write Port                      ${GPIOB}    ${HSLVR}      0x00008001
    Write Port                      ${GPIOB}    ${SECCFGR}    0x00000F0F
    Port Should Be                  ${GPIOB}    ${HSLVR}      0x00008001
    Port Should Be                  ${GPIOB}    ${SECCFGR}    0x00000F0F

Should Lock The Configuration Of Selected Pins
    [Documentation]                 LCKR write sequence of RM0456 section 13.4.8 locks pin 5.
    Write Port                      ${GPIOA}    ${LCKR}    0x00010020
    Write Port                      ${GPIOA}    ${LCKR}    0x00000020
    Write Port                      ${GPIOA}    ${LCKR}    0x00010020
    ${address}=                     Port Register      ${GPIOA}    ${LCKR}
    ${value}=                       Read Word          ${address}
    Port Should Be                  ${GPIOA}    ${LCKR}    0x00010020
    Configure PA5 As Output
    Port Should Be                  ${GPIOA}    ${MODER}    0xABFFFFFF
