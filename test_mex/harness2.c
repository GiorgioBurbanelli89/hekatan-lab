#include <stdio.h>
#include <windows.h>
typedef const char* (*fn)(int,const char* const*);
int main(){
  HMODULE h=LoadLibraryA("symcas.dll");
  if(!h){printf("load fail %lu\n",GetLastError());return 2;}
  fn f=(fn)GetProcAddress(h,"hkmex_str");
  if(!f){printf("no hkmex_str\n");return 3;}
  const char* a[3]={"diff","x^3+2*x","x"};
  const char* r=f(3,a);
  printf("RESULT=[%s]\n", r?r:"(null)");
  const char* b[3]={"diff","atan(x)","x"};
  printf("RESULT2=[%s]\n", f(3,b));
  return 0;
}
