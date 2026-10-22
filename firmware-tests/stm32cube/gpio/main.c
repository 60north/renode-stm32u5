/* STM32CubeU5 HAL GPIO and EXTI test.
 *
 * Uses the unmodified HAL on the test fixture tests/common/stm32u575-gpio-fixture.repl:
 * - PA5: push-pull output (LED). After start-up it is toggled twice with HAL_GPIO_TogglePin and
 *   then mirrors the level of PB13.
 * - PB13: input, read with HAL_GPIO_ReadPin.
 * - PC13: input with EXTI interrupt on the rising edge; HAL_GPIO_EXTI_Rising_Callback counts the
 *   edges.
 * Results are stored in global variables that the robot test reads through the ELF symbols. */

#include "stm32u5xx_hal.h"

volatile uint32_t ready;
volatile uint32_t toggleOk;
volatile uint32_t risingEdges;
volatile uint32_t lastRisingPin;
volatile uint32_t mirroredLevel;
volatile uint32_t loopCount;

void SysTick_Handler(void)
{
    HAL_IncTick();
}

void EXTI13_IRQHandler(void)
{
    HAL_GPIO_EXTI_IRQHandler(GPIO_PIN_13);
}

void HAL_GPIO_EXTI_Rising_Callback(uint16_t pin)
{
    lastRisingPin = pin;
    risingEdges += 1u;
}

static void configurePins(void)
{
    GPIO_InitTypeDef init = {0};

    __HAL_RCC_GPIOA_CLK_ENABLE();
    __HAL_RCC_GPIOB_CLK_ENABLE();
    __HAL_RCC_GPIOC_CLK_ENABLE();

    HAL_GPIO_WritePin(GPIOA, GPIO_PIN_5, GPIO_PIN_RESET);
    init.Pin = GPIO_PIN_5;
    init.Mode = GPIO_MODE_OUTPUT_PP;
    init.Pull = GPIO_NOPULL;
    init.Speed = GPIO_SPEED_FREQ_LOW;
    HAL_GPIO_Init(GPIOA, &init);

    init.Pin = GPIO_PIN_13;
    init.Mode = GPIO_MODE_INPUT;
    init.Pull = GPIO_NOPULL;
    HAL_GPIO_Init(GPIOB, &init);

    init.Pin = GPIO_PIN_13;
    init.Mode = GPIO_MODE_IT_RISING;
    init.Pull = GPIO_NOPULL;
    HAL_GPIO_Init(GPIOC, &init);

    HAL_NVIC_SetPriority(EXTI13_IRQn, 2, 0);
    HAL_NVIC_EnableIRQ(EXTI13_IRQn);
}

int main(void)
{
    HAL_Init();
    configurePins();

    HAL_GPIO_TogglePin(GPIOA, GPIO_PIN_5);
    const GPIO_PinState afterFirst = HAL_GPIO_ReadPin(GPIOA, GPIO_PIN_5);
    HAL_GPIO_TogglePin(GPIOA, GPIO_PIN_5);
    const GPIO_PinState afterSecond = HAL_GPIO_ReadPin(GPIOA, GPIO_PIN_5);
    toggleOk = (afterFirst == GPIO_PIN_SET) && (afterSecond == GPIO_PIN_RESET);

    ready = 1u;
    for (;;)
    {
        const GPIO_PinState level = HAL_GPIO_ReadPin(GPIOB, GPIO_PIN_13);
        mirroredLevel = (uint32_t)level;
        HAL_GPIO_WritePin(GPIOA, GPIO_PIN_5, level);
        loopCount += 1u;
    }
}
