/* STM32CubeU5 HAL UART test.
 *
 * Unmodified HAL, USART1 at 115200 baud from PCLK2 = 160 MHz, initialised as STM32CubeMX does
 * (HAL_UART_Init, FIFO thresholds, FIFO mode disabled):
 * - prints a banner with HAL_UART_Transmit (polling) and a second line with
 *   HAL_UART_Transmit_IT (interrupt);
 * - receives characters one by one with HAL_UART_Receive_IT and echoes them in upper case
 *   with HAL_UART_Transmit. */

#include <string.h>

#include "stm32u5xx_hal.h"

UART_HandleTypeDef huart1;

volatile uint32_t step;
volatile uint32_t failedStep;
volatile uint32_t transmitComplete;
volatile uint32_t receivedCount;
volatile uint32_t usartBrr;

static volatile uint8_t rxByte;
static volatile uint8_t ring[32];
static volatile uint32_t ringHead;
static volatile uint32_t ringTail;

static const char banner[] = "STM32U575 HAL UART ready\r\n";
static const char interruptLine[] = "Interrupt transmit done\r\n";

void SysTick_Handler(void)
{
    HAL_IncTick();
}

void USART1_IRQHandler(void)
{
    HAL_UART_IRQHandler(&huart1);
}

static void fail(uint32_t at)
{
    failedStep = at;
    for (;;)
    {
    }
}

static void check(HAL_StatusTypeDef status, uint32_t at)
{
    step = at;
    if (status != HAL_OK)
    {
        fail(at);
    }
}

void HAL_UART_MspInit(UART_HandleTypeDef *huart)
{
    if (huart->Instance == USART1)
    {
        GPIO_InitTypeDef init = {0};
        __HAL_RCC_USART1_CLK_ENABLE();
        __HAL_RCC_GPIOA_CLK_ENABLE();
        /* PA9 TX, PA10 RX, AF7 (STM32U575 datasheet alternate function table). */
        init.Pin = GPIO_PIN_9 | GPIO_PIN_10;
        init.Mode = GPIO_MODE_AF_PP;
        init.Pull = GPIO_NOPULL;
        init.Speed = GPIO_SPEED_FREQ_LOW;
        init.Alternate = GPIO_AF7_USART1;
        HAL_GPIO_Init(GPIOA, &init);
        HAL_NVIC_SetPriority(USART1_IRQn, 1, 0);
        HAL_NVIC_EnableIRQ(USART1_IRQn);
    }
}

void HAL_UART_TxCpltCallback(UART_HandleTypeDef *huart)
{
    if (huart == &huart1)
    {
        transmitComplete = 1u;
    }
}

void HAL_UART_RxCpltCallback(UART_HandleTypeDef *huart)
{
    if (huart == &huart1)
    {
        ring[ringHead % sizeof(ring)] = rxByte;
        ringHead += 1u;
        receivedCount += 1u;
        (void)HAL_UART_Receive_IT(&huart1, (uint8_t *)&rxByte, 1);
    }
}

static void systemClockConfig(void)
{
    RCC_OscInitTypeDef oscillators = {0};
    RCC_ClkInitTypeDef clocks = {0};

    __HAL_RCC_PWR_CLK_ENABLE();
    check(HAL_PWREx_ControlVoltageScaling(PWR_REGULATOR_VOLTAGE_SCALE1), 1);

    oscillators.OscillatorType = RCC_OSCILLATORTYPE_MSI;
    oscillators.MSIState = RCC_MSI_ON;
    oscillators.MSICalibrationValue = RCC_MSICALIBRATION_DEFAULT;
    oscillators.MSIClockRange = RCC_MSIRANGE_4;
    oscillators.PLL.PLLState = RCC_PLL_ON;
    oscillators.PLL.PLLSource = RCC_PLLSOURCE_MSI;
    oscillators.PLL.PLLMBOOST = RCC_PLLMBOOST_DIV1;
    oscillators.PLL.PLLM = 1;
    oscillators.PLL.PLLN = 80;
    oscillators.PLL.PLLP = 2;
    oscillators.PLL.PLLQ = 2;
    oscillators.PLL.PLLR = 2;
    oscillators.PLL.PLLRGE = RCC_PLLVCIRANGE_0;
    check(HAL_RCC_OscConfig(&oscillators), 2);

    clocks.ClockType = RCC_CLOCKTYPE_HCLK | RCC_CLOCKTYPE_SYSCLK | RCC_CLOCKTYPE_PCLK1 |
                       RCC_CLOCKTYPE_PCLK2 | RCC_CLOCKTYPE_PCLK3;
    clocks.SYSCLKSource = RCC_SYSCLKSOURCE_PLLCLK;
    clocks.AHBCLKDivider = RCC_SYSCLK_DIV1;
    clocks.APB1CLKDivider = RCC_HCLK_DIV1;
    clocks.APB2CLKDivider = RCC_HCLK_DIV1;
    clocks.APB3CLKDivider = RCC_HCLK_DIV1;
    check(HAL_RCC_ClockConfig(&clocks, FLASH_LATENCY_4), 3);
}

static void usart1Init(void)
{
    huart1.Instance = USART1;
    huart1.Init.BaudRate = 115200;
    huart1.Init.WordLength = UART_WORDLENGTH_8B;
    huart1.Init.StopBits = UART_STOPBITS_1;
    huart1.Init.Parity = UART_PARITY_NONE;
    huart1.Init.Mode = UART_MODE_TX_RX;
    huart1.Init.HwFlowCtl = UART_HWCONTROL_NONE;
    huart1.Init.OverSampling = UART_OVERSAMPLING_16;
    huart1.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
    huart1.Init.ClockPrescaler = UART_PRESCALER_DIV1;
    huart1.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
    check(HAL_UART_Init(&huart1), 4);
    check(HAL_UARTEx_SetTxFifoThreshold(&huart1, UART_TXFIFO_THRESHOLD_1_8), 5);
    check(HAL_UARTEx_SetRxFifoThreshold(&huart1, UART_RXFIFO_THRESHOLD_1_8), 6);
    check(HAL_UARTEx_DisableFifoMode(&huart1), 7);
    usartBrr = USART1->BRR;
}

int main(void)
{
    check(HAL_Init(), 0);
    systemClockConfig();
    usart1Init();

    check(HAL_UART_Transmit(&huart1, (const uint8_t *)banner, strlen(banner), 100), 8);

    check(HAL_UART_Transmit_IT(&huart1, (const uint8_t *)interruptLine, strlen(interruptLine)), 9);
    const uint32_t start = HAL_GetTick();
    while (!transmitComplete)
    {
        if (HAL_GetTick() - start > 100u)
        {
            fail(10);
        }
    }

    check(HAL_UART_Receive_IT(&huart1, (uint8_t *)&rxByte, 1), 11);
    step = 12;

    for (;;)
    {
        if (ringTail != ringHead)
        {
            uint8_t c = ring[ringTail % sizeof(ring)];
            ringTail += 1u;
            if (c >= 'a' && c <= 'z')
            {
                c = (uint8_t)(c - 'a' + 'A');
            }
            (void)HAL_UART_Transmit(&huart1, &c, 1, 100);
        }
    }
}
