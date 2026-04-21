#include "led.h"
#include "delay.h"
#include "sys.h"
#include "key.h"
#include "usart.h"
#include "MPU6050.h"
//ALIENTEK Mini STM32开发板范例代码3
//串口实验
//技术论坛:www.openedv.com	

 int main(void)
 {
	u8 t;
	u8 len;	
	u16 times=0;  
	u16 acc_x,acc_y,acc_z,gy_x,gy_y,gy_z;	
 	SystemInit();//系统时钟等初始化
	delay_init(72);	     //延时初始化
	NVIC_Configuration();//设置NVIC中断分组2:2位抢占优先级，2位响应优先级
	uart_init(115200);//串口初始化为9600
 	LED_Init();	 //LED端口初始化
	InitMPU6050();
	while(1)
	{
		acc_x= GetData(ACCEL_XOUT_H);
		acc_y= GetData(ACCEL_YOUT_H);
		acc_z= GetData(ACCEL_ZOUT_H);
		gy_x= GetData(GYRO_XOUT_H);
		gy_y= GetData(GYRO_YOUT_H);
		gy_z= GetData(GYRO_ZOUT_H);

		printf("ACC:  X=%d   Y=%d   Z=%d  \n",acc_x,acc_y,acc_z);
		printf("GYRO:  X=%d   Y=%d   Z=%d  \n",gy_x,gy_y,gy_z);
		delay_ms(500);   
	}	 

 }

