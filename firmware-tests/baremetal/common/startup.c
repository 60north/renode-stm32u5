/* Minimal start-up code for STM32U575 bare-metal test firmware.
 * Vector table size: 16 system exceptions + IRQ 0..125 (stm32u575xx.h IRQn_Type). */

#include <stdint.h>

#include "test_support.h"

#define STM32U575_IRQ_COUNT 126u

extern uint32_t _estack;
extern uint32_t _sidata;
extern uint32_t _sdata;
extern uint32_t _edata;
extern uint32_t _sbss;
extern uint32_t _ebss;

int main(void);

volatile uint32_t resetHandlerCount __attribute__((section(".sram4")));
volatile uint32_t faultCount;

void Default_Handler(void)
{
    faultCount += 1u;
    for (;;)
    {
    }
}

void Reset_Handler(void)
{
    resetHandlerCount += 1u;

    const uint32_t *source = &_sidata;
    for (uint32_t *destination = &_sdata; destination < &_edata; ++destination)
    {
        *destination = *source++;
    }
    for (uint32_t *destination = &_sbss; destination < &_ebss; ++destination)
    {
        *destination = 0u;
    }

    (void)main();
    for (;;)
    {
    }
}

void NMI_Handler(void) __attribute__((weak, alias("Default_Handler")));
void HardFault_Handler(void) __attribute__((weak, alias("Default_Handler")));
void MemManage_Handler(void) __attribute__((weak, alias("Default_Handler")));
void BusFault_Handler(void) __attribute__((weak, alias("Default_Handler")));
void UsageFault_Handler(void) __attribute__((weak, alias("Default_Handler")));
void SecureFault_Handler(void) __attribute__((weak, alias("Default_Handler")));
void SVC_Handler(void) __attribute__((weak, alias("Default_Handler")));
void DebugMon_Handler(void) __attribute__((weak, alias("Default_Handler")));
void PendSV_Handler(void) __attribute__((weak, alias("Default_Handler")));
void SysTick_Handler(void) __attribute__((weak, alias("Default_Handler")));
void Irq_Handler(void) __attribute__((weak, alias("Default_Handler")));

typedef void (*VectorEntry)(void);

__attribute__((section(".isr_vector"), used))
const VectorEntry vectorTable[16u + STM32U575_IRQ_COUNT] = {
    (VectorEntry)(uintptr_t)&_estack,
    Reset_Handler,
    NMI_Handler,
    HardFault_Handler,
    MemManage_Handler,
    BusFault_Handler,
    UsageFault_Handler,
    SecureFault_Handler,
    0,
    0,
    0,
    SVC_Handler,
    DebugMon_Handler,
    0,
    PendSV_Handler,
    SysTick_Handler,
    [16 ... 16u + STM32U575_IRQ_COUNT - 1u] = Irq_Handler,
};
