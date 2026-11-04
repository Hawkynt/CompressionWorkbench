static const char message[] = "CompressionWorkbench PE fixture";
volatile int counter = 7;
int start(void) { return message[counter] + counter; }
