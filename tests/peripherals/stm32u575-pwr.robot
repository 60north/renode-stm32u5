*** Settings ***
Documentation       STM32U575 PWR behaviour at register level (RM0456 Rev 7 section 10).
...                 Register offsets and bit positions: stm32u575xx.h.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine

*** Variables ***
${PWR_CR3}          0x46020808
${PWR_VOSR}         0x4602080C
${PWR_DBPR}         0x46020828
${PWR_SVMSR}        0x4602083C

${BOOSTRDY}         0x00004000
${VOSRDY}           0x00008000
${BOOSTEN}          0x00040000
${ACTVOSRDY}        0x00008000
${REGS}             0x00000002

*** Test Cases ***
Should Report Each Voltage Range As Applied And Ready
    [Documentation]                 VOS 0 = range 4 ... 3 = range 1 (RM0456 section 10.10.4).
    FOR    ${vos}    IN RANGE    4
        ${value}=                   Evaluate           ${vos} << 16
        Write Word                  ${PWR_VOSR}        ${value}
        Field Should Be             ${PWR_VOSR}        16    2    ${vos}
        Bits Should Be Set          ${PWR_VOSR}        ${VOSRDY}
        Field Should Be             ${PWR_SVMSR}       16    2    ${vos}
        Bits Should Be Set          ${PWR_SVMSR}       ${ACTVOSRDY}
    END

Should Report The Booster Ready Only While Enabled
    Bits Should Be Clear            ${PWR_VOSR}        ${BOOSTRDY}
    ${value}=                       Evaluate           (3 << 16) | ${BOOSTEN}
    Write Word                      ${PWR_VOSR}        ${value}
    Bits Should Be Set              ${PWR_VOSR}        ${BOOSTEN} | ${BOOSTRDY}
    Write Word                      ${PWR_VOSR}        0x00030000
    Bits Should Be Clear            ${PWR_VOSR}        ${BOOSTRDY}

Should Not Let Software Write The Ready Flags
    Write Word                      ${PWR_VOSR}        0x0000C000
    Word Should Be                  ${PWR_VOSR}        ${VOSRDY}

Should Report The Selected Regulator
    Bits Should Be Clear            ${PWR_SVMSR}       ${REGS}
    Write Word                      ${PWR_CR3}         0x00000002
    Bits Should Be Set              ${PWR_SVMSR}       ${REGS}
    Write Word                      ${PWR_CR3}         0x00000000
    Bits Should Be Clear            ${PWR_SVMSR}       ${REGS}

Should Keep The Backup Domain Write Enable
    Word Should Be                  ${PWR_DBPR}        0
    Write Word                      ${PWR_DBPR}        0x1
    Word Should Be                  ${PWR_DBPR}        0x1
