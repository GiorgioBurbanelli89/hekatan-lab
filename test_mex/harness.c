#include <stdio.h>
#include <windows.h>
static double buf[64];
double* myalloc(int idx,int r,int c){ (void)idx; return buf; }
typedef void (*fn)(int,const double* const*,const int*,const int*,int,double*(*)(int,int,int),int*,int*);
int main(){
  HMODULE h=LoadLibraryA("suma_f.dll");
  if(!h){ printf("LoadLibrary fail %lu\n",GetLastError()); return 2;}
  fn f=(fn)GetProcAddress(h,"hkmex");
  if(!f){ printf("no hkmex\n"); return 3;}
  double A[4]={1,2,3,4}, B[4]={10,20,30,40};
  const double* in[2]={A,B}; int rows[2]={2,2}, cols[2]={2,2};
  int orow[1]={0}, ocol[1]={0};
  f(2,in,rows,cols,1,myalloc,orow,ocol);
  printf("out %dx%d: %g %g %g %g\n",orow[0],ocol[0],buf[0],buf[1],buf[2],buf[3]);
  return 0;
}
