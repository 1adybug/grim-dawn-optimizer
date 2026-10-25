#pragma once
#include <algorithm>
#include <cmath>
// Use the same available-energy ceiling as the game's potion activation path.
inline float availableMana(float total,float reserved){
    if(!std::isfinite(total)||!std::isfinite(reserved)||total<=0)return 0;
    return std::max(0.f,total-std::max(0.f,reserved));
}
inline bool needsPotion(double current,float ceiling,float recovery){
    return std::isfinite(current)&&std::isfinite(ceiling)&&std::isfinite(recovery)
        &&current>=0&&ceiling>0&&recovery>0&&static_cast<double>(ceiling)-current>=recovery;
}
inline float recoveryAmount(float flat,float percent,float extraPercent,float ceiling,float healIncrease,float percentHealIncrease){
    if(!std::isfinite(flat)||!std::isfinite(percent)||!std::isfinite(extraPercent)||!std::isfinite(ceiling)
       ||!std::isfinite(healIncrease)||!std::isfinite(percentHealIncrease)||ceiling<=0)return 0;
    float amount=flat*(1.f+healIncrease*.01f)
        +std::max(0.f,ceiling*percent*.01f*(1.f+(healIncrease+percentHealIncrease)*.01f))
        +std::max(0.f,ceiling*extraPercent*.01f*(1.f+healIncrease*.01f));
    return std::isfinite(amount)&&amount>0?amount:0;
}
